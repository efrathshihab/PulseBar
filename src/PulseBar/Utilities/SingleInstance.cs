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

    private readonly SafeWaitHandle _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(SafeWaitHandle mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>
    /// প্রথম instance হলে object ফেরত দেয়। আগে থেকে কেউ চললে তাকে popup খোলার
    /// সংকেত পাঠিয়ে null ফেরত দেয়, তখন এই process বন্ধ হয়ে যাবে।
    /// </summary>
    public static SingleInstance? TryAcquire()
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

            var mutex = CreateMutexW(ref attributes, false, MutexName);
            int error = Marshal.GetLastWin32Error();

            if (mutex.IsInvalid)
            {
                mutex.Dispose();

                // ACCESS_DENIED মানে আছে কিন্তু খুলতে পারছি না, সেটাও "আগে থেকে চলছে"
                if (error == ERROR_ACCESS_DENIED)
                {
                    SignalExisting();
                    return null;
                }

                // অন্য কোনো কারণে Mutex বানানো গেল না: single instance ছাড়াই চলবে
                return Unguarded();
            }

            if (error == ERROR_ALREADY_EXISTS)
            {
                mutex.Dispose();
                SignalExisting();
                return null;
            }

            var eventHandle = CreateEventW(ref attributes, false, false, ShowEventName);
            if (eventHandle.IsInvalid)
            {
                eventHandle.Dispose();
                return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset));
            }

            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = eventHandle };
            return new SingleInstance(mutex, showEvent);
        }
        catch
        {
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

    /// <summary>দ্বিতীয় instance চালু হওয়ার চেষ্টা করলে (background thread থেকে) ডাকা হয়।</summary>
    public void ListenForActivation(Action onActivate)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => onActivate(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _showEvent.Dispose();
        _mutex.Dispose();
    }

    /// <summary>Mutex ছাড়া চলার জন্য (crash না করে); তখন একাধিক instance আটকানো যাবে না।</summary>
    private static SingleInstance Unguarded()
    {
        return new SingleInstance(new SafeWaitHandle(IntPtr.Zero, false), new EventWaitHandle(false, EventResetMode.AutoReset));
    }

    private static void SignalExisting()
    {
        // চালু থাকা PulseBar যেন popup কে সামনে আনতে পারে
        AllowSetForegroundWindow(ASFW_ANY);

        using var existing = OpenEventW(EVENT_MODIFY_STATE, false, ShowEventName);
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
