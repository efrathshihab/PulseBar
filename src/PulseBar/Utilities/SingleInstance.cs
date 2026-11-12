using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PulseBar.Utilities;

/// <summary>
/// একসাথে একটাই PulseBar চালায়। দ্বিতীয়টা চালু হলে প্রথমটাকে সংকেত পাঠিয়ে popup খোলায়।
/// </summary>
/// <remarks>
/// Admin হিসেবে চলা process এর বানানো object সাধারণ process খুলতে পারে না (DACL আর
/// integrity level দুটোর কারণেই)। তাই Mutex আর Event নিজস্ব security দিয়ে বানানো হয়:
/// শুধু এই user, SYSTEM আর Administrators, আর Medium integrity label, যাতে admin আর
/// সাধারণ দুই অবস্থাতেই একে অপরকে দেখতে পায়। .NET এর MutexAcl এই label বসাতে
/// বাড়তি privilege চায়, তাই সরাসরি Win32 API।
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PulseBar.SingleInstance";
    private const string ShowEventName = @"Local\PulseBar.ShowPopup";
    private const string QuitEventName = @"Local\PulseBar.Quit";

    private static readonly TimeSpan ReplaceTimeout = TimeSpan.FromSeconds(5);

    private readonly SafeWaitHandle _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly EventWaitHandle _quitEvent;
    private RegisteredWaitHandle? _showWait;
    private RegisteredWaitHandle? _quitWait;

    private SingleInstance(SafeWaitHandle mutex, EventWaitHandle showEvent, EventWaitHandle quitEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _quitEvent = quitEvent;
    }

    /// <summary>
    /// প্রথম instance হলে object ফেরত দেয়। আগে থেকে কেউ চললে:
    /// <paramref name="replaceExisting"/> false হলে তাকে popup খোলার সংকেত পাঠিয়ে null ফেরত দেয়
    /// (তখন এই process বন্ধ হবে); true হলে (Debug build) তাকে Quit করতে বলে, সর্বোচ্চ ৫ সেকেন্ড
    /// অপেক্ষা করে নিজেই প্রথম instance হয়।
    /// </summary>
    public static SingleInstance? TryAcquire(bool replaceExisting)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(BuildSddl(), SDDL_REVISION_1, out descriptor, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = false,
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool askedToQuit = false;

            while (true)
            {
                var mutex = CreateMutexW(ref attributes, false, MutexName);
                int error = Marshal.GetLastWin32Error();

                // ACCESS_DENIED মানে আছে কিন্তু খুলতে পারছি না, সেটাও "আগে থেকে চলছে"
                bool exists = mutex.IsInvalid ? error == ERROR_ACCESS_DENIED : error == ERROR_ALREADY_EXISTS;

                if (mutex.IsInvalid && !exists)
                {
                    // অন্য কোনো কারণে Mutex বানানো গেল না: single instance ছাড়াই চলবে
                    mutex.Dispose();
                    Log.Error($"Single instance mutex failed (Win32 {error}), running unguarded");
                    return Unguarded();
                }

                if (!exists)
                {
                    if (askedToQuit)
                    {
                        Log.Info($"Previous instance exited after {stopwatch.ElapsedMilliseconds} ms");
                    }

                    return Create(mutex, ref attributes);
                }

                // আমাদের handle থাকলে Mutex টা বেঁচে থাকে, তাই অপেক্ষার সময় ছেড়ে দিতে হবে
                mutex.Dispose();

                if (!replaceExisting)
                {
                    SignalExisting(ShowEventName, allowForeground: true);
                    return null;
                }

                if (!askedToQuit)
                {
                    askedToQuit = true;
                    Log.Info("Dev build: asking the running instance to quit");
                    SignalExisting(QuitEventName, allowForeground: false);
                }

                if (stopwatch.Elapsed > ReplaceTimeout)
                {
                    Log.Error("Previous instance did not quit within 5 s; showing its popup instead");
                    SignalExisting(ShowEventName, allowForeground: true);
                    return null;
                }

                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Single instance check failed, running unguarded", ex);
            return Unguarded();
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
            {
                LocalFree(descriptor);
            }
        }
    }

    private static SingleInstance Create(SafeWaitHandle mutex, ref SECURITY_ATTRIBUTES attributes)
    {
        return new SingleInstance(mutex, CreateNamedEvent(ShowEventName, ref attributes), CreateNamedEvent(QuitEventName, ref attributes));
    }

    private static EventWaitHandle CreateNamedEvent(string name, ref SECURITY_ATTRIBUTES attributes)
    {
        var handle = CreateEventW(ref attributes, false, false, name);
        if (handle.IsInvalid)
        {
            // নাম ছাড়া event: অন্য instance সংকেত দিতে পারবে না, তবে app চলবে
            handle.Dispose();
            return new EventWaitHandle(false, EventResetMode.AutoReset);
        }

        return new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
    }

    /// <summary>দ্বিতীয় instance চালু হওয়ার চেষ্টা করলে (background thread থেকে) ডাকা হয়।</summary>
    public void ListenForActivation(Action onActivate)
    {
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>নতুন Debug build চালু হয়ে এটাকে বন্ধ হতে বললে (background thread থেকে) ডাকা হয়।</summary>
    public void ListenForQuit(Action onQuit)
    {
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => onQuit(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _quitWait?.Unregister(null);
        _showEvent.Dispose();
        _quitEvent.Dispose();
        _mutex.Dispose();
    }

    /// <summary>Mutex ছাড়া চলার জন্য (crash না করে); তখন একাধিক instance আটকানো যাবে না।</summary>
    private static SingleInstance Unguarded()
    {
        return new SingleInstance(
            new SafeWaitHandle(IntPtr.Zero, false),
            new EventWaitHandle(false, EventResetMode.AutoReset),
            new EventWaitHandle(false, EventResetMode.AutoReset));
    }

    private static void SignalExisting(string eventName, bool allowForeground)
    {
        // চালু থাকা PulseBar যেন popup কে সামনে আনতে পারে
        if (allowForeground)
        {
            AllowSetForegroundWindow(ASFW_ANY);
        }

        using var existing = OpenEventW(EVENT_MODIFY_STATE, false, eventName);
        if (!existing.IsInvalid)
        {
            SetEvent(existing);
        }
    }

    private static string BuildSddl()
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? "IU";

        // DACL: SYSTEM, Administrators, এই user, সবার full access
        // SACL: Medium integrity label, No-Write-Up, যাতে admin এর object এ সাধারণ process লিখতে পারে
        return $"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{user})S:(ML;;NW;;;ME)";
    }

    private const int ASFW_ANY = -1;
    private const uint SDDL_REVISION_1 = 1;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_ALREADY_EXISTS = 183;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexW(ref SECURITY_ATTRIBUTES attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(ref SECURITY_ATTRIBUTES attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEventW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(SafeWaitHandle handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
