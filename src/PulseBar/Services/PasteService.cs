using System.Diagnostics;
using System.Runtime.InteropServices;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Clipboard panel এর paste: panel খোলার আগে সামনে থাকা window মনে রাখে, পরে সেখানে focus
/// ফিরিয়ে SendInput দিয়ে Ctrl+V পাঠায়। UI thread থেকে ডাকতে হবে।
/// </summary>
/// <remarks>
/// Windows (UIPI) কম integrity এর process থেকে বেশি integrity এর window এ SendInput আটকে দেয়,
/// আর SendInput এর ফলাফলে সেটা বোঝা যায় না। তাই আগেই দুই process এর integrity level মেলানো হয়।
/// </remarks>
public sealed class PasteService
{
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromMilliseconds(300);

    private readonly int _ownIntegrity = IntegrityOf(Environment.ProcessId) ?? SECURITY_MANDATORY_MEDIUM_RID;
    private IntPtr _target;

    /// <summary>এখন সামনে থাকা window মনে রাখে; সেটা PulseBar এর নিজের হলে কিছুই মনে রাখে না।</summary>
    public void RememberTarget()
    {
        var hwnd = GetForegroundWindow();
        _target = hwnd != IntPtr.Zero && ProcessIdOf(hwnd) != Environment.ProcessId ? hwnd : IntPtr.Zero;
    }

    public bool HasTarget => _target != IntPtr.Zero && IsWindow(_target);

    /// <summary>
    /// আগের window টা PulseBar এর চেয়ে বেশি integrity তে (যেমন admin হিসেবে) চলছে, তাই Ctrl+V
    /// পাঠানো যাবে না। Integrity পড়া না গেলেও (protected process ইত্যাদি) true।
    /// </summary>
    public bool IsTargetBlocked()
    {
        if (!HasTarget)
        {
            return false;
        }

        int? integrity = IntegrityOf(ProcessIdOf(_target));
        return integrity is null || integrity > _ownIntegrity;
    }

    /// <summary>আগের window কে আবার সামনে আনে। এর আগে PulseBar এর নিজের user input পেতে হবে।</summary>
    public void RestoreFocus()
    {
        if (HasTarget)
        {
            if (IsIconic(_target))
            {
                ShowWindow(_target, SW_RESTORE);
            }

            SetForegroundWindow(_target);
        }
    }

    /// <summary>
    /// আগের window সামনে আসা পর্যন্ত (সর্বোচ্চ ৩০০ ms) অপেক্ষা করে Ctrl+V পাঠায়। অন্য window সামনে
    /// থাকলে পাঠায় না, যাতে ভুল জায়গায় paste না হয়। সফল হলে true।
    /// </summary>
    public async Task<bool> PasteAsync()
    {
        if (!HasTarget)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        while (GetForegroundWindow() != _target)
        {
            if (stopwatch.Elapsed > FocusTimeout)
            {
                Log.Error($"Auto-paste skipped: focus did not return in {FocusTimeout.TotalMilliseconds:0} ms");
                return false;
            }

            await Task.Delay(10);
        }

        var inputs = new[]
        {
            Key(VK_CONTROL, up: false),
            Key(VK_V, up: false),
            Key(VK_V, up: true),
            Key(VK_CONTROL, up: true),
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Log.Error($"Auto-paste: SendInput sent {sent}/{inputs.Length} (Win32 {Marshal.GetLastPInvokeError()})");
            return false;
        }

        return true;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
        },
    };

    private static int ProcessIdOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    /// <summary>Process এর mandatory integrity RID (Medium = 0x2000, High = 0x3000); না পারলে null।</summary>
    private static int? IntegrityOf(int processId)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out token))
            {
                return null;
            }

            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
            {
                return null;
            }

            // TOKEN_MANDATORY_LABEL এর প্রথম field SID এর pointer; শেষ sub-authority টাই level
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }

            CloseHandle(process);
        }
    }

    private const int SECURITY_MANDATORY_MEDIUM_RID = 0x2000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int SW_RESTORE = 9;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // MOUSEINPUT সবচেয়ে বড়, তাই union এর মাপ ঠিক রাখতে সেটাও রাখা হয়
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
}
