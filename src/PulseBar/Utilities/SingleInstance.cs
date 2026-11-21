using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PulseBar.Utilities;

/// <summary>
/// Runs only one PulseBar at a time. When a second one starts, it signals the first to open its popup.
/// </summary>
/// <remarks>
/// Objects created by an admin process can't be opened by a normal process (because of both the DACL and
/// the integrity level). So the Mutex and Events are created with their own security:
/// only this user, SYSTEM and Administrators, plus a Medium integrity label, so admin and
/// normal instances can see each other. .NET's MutexAcl needs an extra privilege to set this label,
/// so the Win32 API is used directly.
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
    /// Returns an object if this is the first instance. If one is already running:
    /// with <paramref name="replaceExisting"/> false, signals it to open its popup and returns null
    /// (this process then exits); with true (Debug build), asks it to Quit, waits up to 5 seconds
    /// and becomes the first instance itself. With <paramref name="activateExisting"/> false (started
    /// at login), the running one's popup isn't opened.
    /// </summary>
    public static SingleInstance? TryAcquire(bool replaceExisting, bool activateExisting = true)
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

                // ACCESS_DENIED means it exists but can't be opened, which also means "already running"
                bool exists = mutex.IsInvalid ? error == ERROR_ACCESS_DENIED : error == ERROR_ALREADY_EXISTS;

                if (mutex.IsInvalid && !exists)
                {
                    // Couldn't create the Mutex for some other reason: run without single-instance protection
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

                // Our handle keeps the Mutex alive, so release it while waiting
                mutex.Dispose();

                if (!replaceExisting)
                {
                    if (activateExisting)
                    {
                        SignalExisting(ShowEventName, allowForeground: true);
                    }

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
                    if (activateExisting)
                    {
                        SignalExisting(ShowEventName, allowForeground: true);
                    }

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
            // Unnamed event: other instances can't signal, but the app keeps running
            handle.Dispose();
            return new EventWaitHandle(false, EventResetMode.AutoReset);
        }

        return new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
    }

    /// <summary>Called (from a background thread) when a second instance tries to start.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Called (from a background thread) when a newer Debug build starts and asks this one to exit.</summary>
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

    /// <summary>For running without the Mutex (instead of crashing); multiple instances can't be prevented then.</summary>
    private static SingleInstance Unguarded()
    {
        return new SingleInstance(
            new SafeWaitHandle(IntPtr.Zero, false),
            new EventWaitHandle(false, EventResetMode.AutoReset),
            new EventWaitHandle(false, EventResetMode.AutoReset));
    }

    private static void SignalExisting(string eventName, bool allowForeground)
    {
        // So the running PulseBar can bring its popup to the front
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

        // DACL: full access for SYSTEM, Administrators and this user
        // SACL: Medium integrity label, No-Write-Up, so normal processes can write to admin-created objects
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
