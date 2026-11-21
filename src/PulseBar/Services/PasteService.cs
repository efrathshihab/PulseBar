using System.Diagnostics;
using System.Runtime.InteropServices;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Paste for the clipboard panel: remembers the window in front before the panel opens, then returns
/// focus there and sends Ctrl+V with SendInput. Must be called from the UI thread.
/// </summary>
/// <remarks>
/// Windows (UIPI) blocks SendInput from a lower-integrity process to a higher-integrity window,
/// and SendInput's result doesn't reveal it. So the two processes' integrity levels are compared first.
/// </remarks>
public sealed class PasteService
{
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromMilliseconds(300);

    private readonly int _ownIntegrity = IntegrityOf(Environment.ProcessId) ?? SECURITY_MANDATORY_MEDIUM_RID;
    private IntPtr _target;

    /// <summary>Remembers the window in front now; remembers nothing if it's PulseBar's own.</summary>
    public void RememberTarget()
    {
        var hwnd = GetForegroundWindow();
        _target = hwnd != IntPtr.Zero && ProcessIdOf(hwnd) != Environment.ProcessId ? hwnd : IntPtr.Zero;
    }

    public bool HasTarget => _target != IntPtr.Zero && IsWindow(_target);

    /// <summary>
    /// The previous window runs at a higher integrity than PulseBar (e.g. as admin), so Ctrl+V
    /// can't be sent. Also true when the integrity can't be read (protected processes and the like).
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

    /// <summary>Brings the previous window back to the front. PulseBar must have received user input before this.</summary>
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
    /// Waits (up to 300 ms) for the previous window to come to the front, then sends Ctrl+V. Doesn't send if
    /// another window is in front, so nothing is pasted in the wrong place. True on success.
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

    /// <summary>The process's mandatory integrity RID (Medium = 0x2000, High = 0x3000); null on failure.</summary>
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

            // The first field of TOKEN_MANDATORY_LABEL points to the SID; the last sub-authority is the level
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

    // MOUSEINPUT is the largest, so it's kept to get the union size right
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
