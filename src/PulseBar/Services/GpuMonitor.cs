using System.Runtime.InteropServices;

namespace PulseBar.Services;

/// <summary>
/// Task Manager এর মতো করে GPU usage আর VRAM পড়ে (PDH এর GPU counter থেকে)।
/// একাধিক GPU থাকলে যেটার dedicated VRAM সবচেয়ে বেশি, সেটাকে main GPU ধরে।
/// </summary>
public sealed unsafe class GpuMonitor : IDisposable
{
    private const string EngineCounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const string MemoryCounterPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

    private readonly IntPtr _query;
    private readonly IntPtr _engineCounter;
    private readonly IntPtr _memoryCounter;
    private readonly bool _pdhReady;

    /// <summary>Main GPU এর LUID (PDH instance নামের সাথে মেলানোর জন্য)। null মানে DXGI থেকে পাওয়া যায়নি।</summary>
    private readonly long? _mainLuid;

    // প্রতি সেকেন্ডে নতুন করে না বানিয়ে বারবার ব্যবহার
    private IntPtr _buffer;
    private uint _bufferSize;
    private readonly Dictionary<long, Dictionary<int, double>> _engineSums = [];

    public GpuMonitor()
    {
        (Name, TotalVramBytes, _mainLuid) = FindMainAdapter();

        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
            {
                return;
            }

            // English path, যাতে non-English Windows এও কাজ করে
            bool engineOk = PdhAddEnglishCounterW(_query, EngineCounterPath, IntPtr.Zero, out _engineCounter) == 0;
            bool memoryOk = PdhAddEnglishCounterW(_query, MemoryCounterPath, IntPtr.Zero, out _memoryCounter) == 0;
            if (!engineOk)
            {
                _engineCounter = IntPtr.Zero;
            }

            if (!memoryOk)
            {
                _memoryCounter = IntPtr.Zero;
            }

            _pdhReady = engineOk || memoryOk;
            if (_pdhReady)
            {
                // Utilization একটা rate counter, তাই প্রথম sample শুধু ভিত্তি
                PdhCollectQueryData(_query);
            }
        }
        catch
        {
            _pdhReady = false;
        }
    }

    /// <summary>Main GPU এর নাম, যেমন "NVIDIA GeForce RTX 3060"। না পেলে null।</summary>
    public string? Name { get; }

    /// <summary>Main GPU এর মোট dedicated VRAM। না পেলে null।</summary>
    public ulong? TotalVramBytes { get; }

    /// <summary>প্রতি সেকেন্ডে ডাকা হয়। কিছু না পেলে মানগুলো null।</summary>
    public void Read(out double? gpuPercent, out ulong? vramUsedBytes)
    {
        gpuPercent = null;
        vramUsedBytes = null;

        if (!_pdhReady)
        {
            return;
        }

        try
        {
            if (PdhCollectQueryData(_query) != 0)
            {
                return;
            }

            if (_engineCounter != IntPtr.Zero)
            {
                gpuPercent = ReadUsage();
            }

            if (_memoryCounter != IntPtr.Zero)
            {
                vramUsedBytes = ReadDedicatedUsage();
            }
        }
        catch
        {
            gpuPercent = null;
            vramUsedBytes = null;
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    /// <summary>
    /// প্রতিটা engine type (3D, Copy, VideoDecode...) এর সব instance যোগ করে,
    /// সবচেয়ে বেশি যে type সেটাই GPU % (Task Manager এর পদ্ধতি)।
    /// </summary>
    private double? ReadUsage()
    {
        if (!GetArray(_engineCounter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out uint count))
        {
            return null;
        }

        foreach (var sums in _engineSums.Values)
        {
            sums.Clear();
        }

        var items = (PDH_FMT_COUNTERVALUE_ITEM*)_buffer;
        for (uint i = 0; i < count; i++)
        {
            var item = items[i];
            if (item.CStatus > PDH_CSTATUS_NEW_DATA)
            {
                continue;
            }

            // যেমন: pid_1234_luid_0x00000000_0x0000D1F2_phys_0_eng_0_engtype_3D
            var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)item.Name);
            if (!TryParseLuid(name, out long luid))
            {
                continue;
            }

            int typeAt = name.IndexOf("engtype_", StringComparison.Ordinal);
            if (typeAt < 0)
            {
                continue;
            }

            int typeKey = string.GetHashCode(name[(typeAt + 8)..]);
            if (!_engineSums.TryGetValue(luid, out var sums))
            {
                sums = [];
                _engineSums[luid] = sums;
            }

            sums.TryGetValue(typeKey, out double current);
            sums[typeKey] = current + item.Value.Double;
        }

        // Main GPU এর মান; DXGI না পেলে যে GPU সবচেয়ে ব্যস্ত তার মান
        double best = 0;
        bool any = false;
        foreach (var (luid, sums) in _engineSums)
        {
            if (_mainLuid is { } main && luid != main)
            {
                continue;
            }

            foreach (double value in sums.Values)
            {
                any = true;
                best = Math.Max(best, value);
            }
        }

        // Main GPU এর কোনো engine এখন কাজ না করলে (instance নেই) সেটা 0%
        return any || count > 0 ? Math.Clamp(best, 0, 100) : null;
    }

    private ulong? ReadDedicatedUsage()
    {
        if (!GetArray(_memoryCounter, PDH_FMT_LARGE, out uint count))
        {
            return null;
        }

        ulong total = 0;
        bool found = false;
        var items = (PDH_FMT_COUNTERVALUE_ITEM*)_buffer;

        for (uint i = 0; i < count; i++)
        {
            var item = items[i];
            if (item.CStatus > PDH_CSTATUS_NEW_DATA)
            {
                continue;
            }

            // যেমন: luid_0x00000000_0x0000D1F2_phys_0
            var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)item.Name);
            if (!TryParseLuid(name, out long luid) || (_mainLuid is { } main && luid != main))
            {
                continue;
            }

            if (item.Value.Large > 0)
            {
                total += (ulong)item.Value.Large;
            }

            found = true;
        }

        return found ? total : null;
    }

    /// <summary>Counter এর সব instance এর মান _buffer এ আনে; buffer দরকার হলে বড় করে।</summary>
    private bool GetArray(IntPtr counter, uint format, out uint count)
    {
        uint size = _bufferSize;
        int status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, _buffer);

        if (status == PDH_MORE_DATA)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
            }

            // কিছুটা বাড়তি রাখা, যাতে নতুন process এলেই আবার বানাতে না হয়
            _bufferSize = size + size / 4 + 1024;
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
            size = _bufferSize;
            status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, _buffer);
        }

        return status == 0;
    }

    /// <summary>"luid_0xHHHHHHHH_0xLLLLLLLL" থেকে 64-bit LUID।</summary>
    private static bool TryParseLuid(ReadOnlySpan<char> name, out long luid)
    {
        luid = 0;
        int at = name.IndexOf("luid_0x", StringComparison.Ordinal);
        if (at < 0 || name.Length < at + 7 + 8 + 3 + 8)
        {
            return false;
        }

        var high = name.Slice(at + 7, 8);
        var low = name.Slice(at + 7 + 8 + 3, 8);
        if (!uint.TryParse(high, System.Globalization.NumberStyles.HexNumber, null, out uint h)
            || !uint.TryParse(low, System.Globalization.NumberStyles.HexNumber, null, out uint l))
        {
            return false;
        }

        luid = ((long)h << 32) | l;
        return true;
    }

    /// <summary>DXGI দিয়ে hardware GPU গুলো দেখে, যেটার dedicated VRAM সবচেয়ে বেশি সেটা।</summary>
    private static (string? Name, ulong? TotalVram, long? Luid) FindMainAdapter()
    {
        IDXGIFactory1? factory = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var obj) != 0)
            {
                return (null, null, null);
            }

            factory = (IDXGIFactory1)obj;
            string? bestName = null;
            ulong bestVram = 0;
            long? bestLuid = null;

            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                try
                {
                    if (adapter.GetDesc1(out var desc) != 0 || (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                    {
                        continue;
                    }

                    ulong vram = (ulong)desc.DedicatedVideoMemory;
                    if (bestLuid is null || vram > bestVram)
                    {
                        bestName = desc.Description.Trim();
                        bestVram = vram;
                        bestLuid = ((long)(uint)desc.AdapterLuidHigh << 32) | desc.AdapterLuidLow;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            return (bestName, bestLuid is null ? null : bestVram, bestLuid);
        }
        catch
        {
            return (null, null, null);
        }
        finally
        {
            if (factory is not null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }
    }

    // ---------- PDH ----------

    private const uint PDH_FMT_LARGE = 0x00000400;
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_VALUE
    {
        [FieldOffset(0)] public double Double;
        [FieldOffset(0)] public long Large;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public uint CStatus;
        public PDH_VALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    // ---------- DXGI ----------

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    // vtable এর ক্রম মানতে হয়; যে method লাগে না সেগুলো শুধু জায়গা রাখার জন্য
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();

        [PreserveSig]
        int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);

        void IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
}
