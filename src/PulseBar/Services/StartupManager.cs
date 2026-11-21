using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.Services;

public enum StartupApplyOutcome
{
    Ok,

    /// <summary>User UAC তে "No" দিয়েছে।</summary>
    Cancelled,

    Failed,
}

public sealed record StartupApplyResult(StartupApplyOutcome Outcome, string? Message = null)
{
    public static StartupApplyResult Ok { get; } = new(StartupApplyOutcome.Ok);
}

/// <summary>
/// Login এ PulseBar চালু করা। Normal: HKCU এর Run key। WithAdmin: Task Scheduler এর "PulseBar"
/// task (logon trigger, highest privileges)। দুটো কখনো একসাথে থাকে না।
/// </summary>
/// <remarks>
/// Highest privileges এর task বানাতে/মুছতে admin লাগে। PulseBar admin হিসেবে না চললে নিজেকেই
/// <see cref="RegisterTaskArg"/> বা <see cref="UnregisterTaskArg"/> দিয়ে "runas" করে চালায়; শুধু
/// তখন UAC আসে, আর ওই helper process task বানিয়ে/মুছে বন্ধ হয়ে যায় (<see cref="RunHelper"/>)।
/// Task পড়া হয় Task Scheduler এর COM দিয়ে, লেখা/মোছা schtasks.exe দিয়ে।
/// </remarks>
public sealed class StartupManager
{
    public const string StartupArg = "--startup";
    public const string RegisterTaskArg = "--register-startup-task";
    public const string UnregisterTaskArg = "--unregister-startup-task";

    public const string TaskName = "PulseBar";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PulseBar";
    private const int ErrorCancelled = 1223;

    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(20);

    public StartupManager(string exePath)
    {
        ExePath = Path.GetFullPath(exePath);
    }

    public string ExePath { get; }

    /// <summary>Run key এ যা লেখা হয়: quotes সহ exe এর path আর <see cref="StartupArg"/>।</summary>
    public string RunCommand => $"\"{ExePath}\" {StartupArg}";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    // ---------------- বাইরে থেকে ----------------

    /// <summary>
    /// Settings এ মান না থাকলে (প্রথমবার, বা file নষ্ট ছিল): এখন আসলে কী আছে। কিছু পড়া না গেলে Off।
    /// </summary>
    public LaunchAtLogin Detect()
    {
        if (QueryTask(out _) == TaskQuery.Present)
        {
            return LaunchAtLogin.WithAdmin;
        }

        return ReadRunValue() is { } value && IsOurRunValue(value) ? LaunchAtLogin.Normal : LaunchAtLogin.Off;
    }

    /// <summary>
    /// <paramref name="mode"/> চালু করে আর অন্যটা মুছে দেয়। দরকার হলে UAC চায়।
    /// Background thread এ চালানো যায়; UI আটকায় না।
    /// </summary>
    public async Task<StartupApplyResult> ApplyAsync(LaunchAtLogin mode)
    {
        try
        {
            switch (mode)
            {
                case LaunchAtLogin.Normal:
                {
                    // আগে task মোছা; না পারলে Run key ও লেখা হবে না, যাতে দুটো একসাথে না থাকে
                    var removed = await RemoveTaskAsync();
                    if (removed.Outcome != StartupApplyOutcome.Ok)
                    {
                        return removed;
                    }

                    WriteRunValue();
                    return StartupApplyResult.Ok;
                }

                case LaunchAtLogin.WithAdmin:
                {
                    var registered = await RegisterTaskAsync();
                    if (registered.Outcome != StartupApplyOutcome.Ok)
                    {
                        return registered;
                    }

                    DeleteRunValue();
                    return StartupApplyResult.Ok;
                }

                default:
                {
                    DeleteRunValue();
                    return await RemoveTaskAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Launch at login: {mode}", ex);
            return new StartupApplyResult(StartupApplyOutcome.Failed, "Windows এ বদলানো যায়নি");
        }
    }

    /// <summary>
    /// App শুরুর সময়: Run key বা task এর path এখনকার exe এর সাথে না মিললে ঠিক করে। UAC কখনো চায় না;
    /// admin ছাড়া task ঠিক করা না গেলে কারণটা ফেরত দেয় (Settings এ দেখানোর জন্য)।
    /// </summary>
    public string? Verify(LaunchAtLogin mode)
    {
        try
        {
            switch (mode)
            {
                case LaunchAtLogin.Normal:
                    if (!string.Equals(ReadRunValue(), RunCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Info("Launch at login: Run key was missing or pointed elsewhere; fixed");
                        WriteRunValue();
                    }

                    return null;

                case LaunchAtLogin.WithAdmin:
                {
                    var state = QueryTask(out var action);
                    if (state == TaskQuery.Unknown || state == TaskQuery.Present && IsOurTaskAction(action))
                    {
                        // পড়া না গেলে কিছু বলা যায় না
                        DeleteRunValueIfOurs();
                        return null;
                    }

                    if (IsElevated)
                    {
                        Log.Info("Launch at login: startup task missing or pointed elsewhere; re-registering");
                        if (RunSchtasks(CreateArgs(WriteTaskXmlFile(CurrentUserSid()))).ExitCode == 0)
                        {
                            DeleteRunValueIfOurs();
                            return null;
                        }
                    }

                    return state == TaskQuery.Missing
                        ? "Login task পাওয়া যায়নি"
                        : "Login task পুরনো exe চালায়";
                }

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Launch at login: verify", ex);
            return null;
        }
    }

    // ---------------- Run key ----------------

    private static string? ReadRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) as string;
    }

    private void WriteRunValue()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(RunValueName, RunCommand, RegistryValueKind.String);
    }

    private static void DeleteRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static void DeleteRunValueIfOurs()
    {
        if (ReadRunValue() is { } value && IsOurRunValue(value))
        {
            DeleteRunValue();
        }
    }

    /// <summary>"PulseBar" নামের value টা কোনো PulseBar.exe চালায় কিনা (অন্য path এরটাও)।</summary>
    private static bool IsOurRunValue(string value) =>
        value.Contains("PulseBar.exe", StringComparison.OrdinalIgnoreCase);

    // ---------------- Task ----------------

    private enum TaskQuery
    {
        Missing,
        Present,

        /// <summary>পড়া গেল না (অনুমতি নেই বা Task Scheduler চলছে না)।</summary>
        Unknown,
    }

    private bool IsOurTaskAction((string Command, string Arguments)? action) =>
        action is { } a &&
        PathsEqual(a.Command.Trim().Trim('"'), ExePath) &&
        string.Equals(a.Arguments.Trim(), StartupArg, StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Task Scheduler এর COM দিয়ে শুধু পড়া; কিছু বদলায় না।</summary>
    private static TaskQuery QueryTask(out (string Command, string Arguments)? action)
    {
        action = null;
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return TaskQuery.Unknown;
            }

            service = Activator.CreateInstance(type);
            if (service is null)
            {
                return TaskQuery.Unknown;
            }

            Invoke(service, "Connect", BindingFlags.InvokeMethod);
            folder = Invoke(service, "GetFolder", BindingFlags.InvokeMethod, @"\");
            if (folder is null)
            {
                return TaskQuery.Unknown;
            }

            try
            {
                task = Invoke(folder, "GetTask", BindingFlags.InvokeMethod, TaskName);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is COMException com && IsNotFound(com.HResult)
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException)
            {
                return TaskQuery.Missing;
            }

            if (task is null || Invoke(task, "Xml", BindingFlags.GetProperty) is not string xml)
            {
                return TaskQuery.Unknown;
            }

            action = ParseTaskAction(xml);
            return TaskQuery.Present;
        }
        catch (Exception ex)
        {
            Log.Error("Launch at login: could not read the startup task", (ex as TargetInvocationException)?.InnerException ?? ex);
            return TaskQuery.Unknown;
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    private static bool IsNotFound(int hresult) =>
        hresult is unchecked((int)0x80070002) or unchecked((int)0x80070003); // file/path not found

    private static object? Invoke(object target, string name, BindingFlags flags, params object?[] args) =>
        target.GetType().InvokeMember(name, flags, null, target, args);

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.FinalReleaseComObject(com);
        }
    }

    /// <summary>Task XML এর প্রথম Exec action।</summary>
    public static (string Command, string Arguments)? ParseTaskAction(string xml)
    {
        try
        {
            var exec = XDocument.Parse(xml).Descendants(TaskNs + "Exec").FirstOrDefault();
            if (exec?.Element(TaskNs + "Command")?.Value is not { } command)
            {
                return null;
            }

            return (command, exec.Element(TaskNs + "Arguments")?.Value ?? "");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Task এর XML: শুধু এই user এর logon এ, highest privileges, কোনো time limit নেই, battery তেও
    /// চলবে আর battery তে গেলে থামবে না।
    /// </summary>
    public static string BuildTaskXml(string exePath, string userSid)
    {
        XElement E(string name, params object?[] content) => new(TaskNs + name, content);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            E("Task",
                new XAttribute("version", "1.2"),
                E("RegistrationInfo",
                    E("Author", "PulseBar"),
                    E("Description", "Starts PulseBar at sign-in with highest privileges (needed for CPU temperature)."),
                    E("URI", @"\" + TaskName)),
                E("Triggers",
                    E("LogonTrigger",
                        E("Enabled", "true"),
                        E("UserId", userSid))),
                E("Principals",
                    E("Principal",
                        new XAttribute("id", "Author"),
                        E("UserId", userSid),
                        E("LogonType", "InteractiveToken"),
                        E("RunLevel", "HighestAvailable"))),
                E("Settings",
                    E("MultipleInstancesPolicy", "IgnoreNew"),
                    E("DisallowStartIfOnBatteries", "false"),
                    E("StopIfGoingOnBatteries", "false"),
                    E("AllowHardTerminate", "true"),
                    E("StartWhenAvailable", "false"),
                    E("RunOnlyIfNetworkAvailable", "false"),
                    E("IdleSettings",
                        E("StopOnIdleEnd", "false"),
                        E("RestartOnIdle", "false")),
                    E("AllowStartOnDemand", "true"),
                    E("Enabled", "true"),
                    E("Hidden", "false"),
                    E("RunOnlyIfIdle", "false"),
                    E("WakeToRun", "false"),
                    E("ExecutionTimeLimit", "PT0S"),
                    // Default 7 মানে below normal; 4 মানে normal priority
                    E("Priority", "4")),
                E("Actions",
                    new XAttribute("Context", "Author"),
                    E("Exec",
                        E("Command", exePath),
                        E("Arguments", StartupArg),
                        E("WorkingDirectory", Path.GetDirectoryName(exePath) ?? "")))));

        var builder = new StringBuilder();
        using (var writer = new StringWriter(builder))
        {
            doc.Save(writer);
        }

        return builder.ToString();
    }

    private async Task<StartupApplyResult> RegisterTaskAsync()
    {
        string sid = CurrentUserSid();
        if (IsElevated)
        {
            return RunSchtasks(CreateArgs(WriteTaskXmlFile(sid))).ExitCode == 0
                ? StartupApplyResult.Ok
                : new StartupApplyResult(StartupApplyOutcome.Failed, "Login task বানানো যায়নি");
        }

        return await RunElevatedHelperAsync($"{RegisterTaskArg} {sid}", "Login task বানানো যায়নি");
    }

    private async Task<StartupApplyResult> RemoveTaskAsync()
    {
        var state = QueryTask(out _);
        if (state == TaskQuery.Missing)
        {
            return StartupApplyResult.Ok;
        }

        // আগে admin ছাড়াই চেষ্টা; highest privileges এর task হলে সাধারণত admin লাগে
        if (RunSchtasks(DeleteArgs()).ExitCode == 0)
        {
            return StartupApplyResult.Ok;
        }

        if (IsElevated)
        {
            return new StartupApplyResult(StartupApplyOutcome.Failed, "Login task মোছা যায়নি");
        }

        return await RunElevatedHelperAsync(UnregisterTaskArg, "Login task মোছা যায়নি");
    }

    /// <summary>নিজেকে "runas" করে helper হিসেবে চালায় আর শেষ হওয়া পর্যন্ত অপেক্ষা করে।</summary>
    private async Task<StartupApplyResult> RunElevatedHelperAsync(string arguments, string failMessage)
    {
        Process? process;
        try
        {
            Log.Info($"Launch at login: asking for admin ({arguments.Split(' ')[0]})");
            process = Process.Start(new ProcessStartInfo(ExePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Launch at login: UAC was declined");
            return new StartupApplyResult(StartupApplyOutcome.Cancelled, "Admin অনুমতি দেওয়া হয়নি, আগের মানে ফেরানো হলো");
        }

        if (process is null)
        {
            return new StartupApplyResult(StartupApplyOutcome.Failed, failMessage);
        }

        using (process)
        {
            using var timeout = new CancellationTokenSource(HelperTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Error("Launch at login: helper did not finish in time");
                return new StartupApplyResult(StartupApplyOutcome.Failed, failMessage);
            }

            if (process.ExitCode != 0)
            {
                Log.Error($"Launch at login: helper exited with {process.ExitCode}");
                return new StartupApplyResult(StartupApplyOutcome.Failed, failMessage);
            }
        }

        return StartupApplyResult.Ok;
    }

    // ---------------- Helper process (admin) ----------------

    /// <summary>App এর argument helper এর কিনা।</summary>
    public static bool IsHelperInvocation(string[] args) =>
        args.Length > 0 && (args[0] == RegisterTaskArg || args[0] == UnregisterTaskArg);

    /// <summary>
    /// Admin হিসেবে চলা helper: task বানায় বা মোছে, তারপর exit code দেয় (0 = সফল)। কোনো window নেই।
    /// Exe এর path argument থেকে নেওয়া হয় না, সবসময় নিজের path।
    /// </summary>
    public static int RunHelper(string[] args)
    {
        try
        {
            if (Environment.ProcessPath is not { } exe)
            {
                return 2;
            }

            if (args[0] == RegisterTaskArg)
            {
                // Task শুধু যে user চেয়েছে তার logon এ; SID ঠিক না হলে নিজের
                string sid = CurrentUserSid();
                if (args.Length > 1)
                {
                    try
                    {
                        sid = new SecurityIdentifier(args[1]).Value;
                    }
                    catch (ArgumentException)
                    {
                        return 3;
                    }
                }

                var manager = new StartupManager(exe);
                return manager.RunSchtasks(CreateArgs(manager.WriteTaskXmlFile(sid))).ExitCode;
            }

            return new StartupManager(exe).RunSchtasks(DeleteArgs()).ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("Startup task helper", ex);
            return 1;
        }
    }

    // ---------------- schtasks ----------------

    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("No user SID");
    }

    private string WriteTaskXmlFile(string sid)
    {
        string path = Path.Combine(Path.GetTempPath(), $"PulseBar-task-{Guid.NewGuid():N}.xml");

        // schtasks UTF-16 (BOM সহ) XML ভালো বোঝে
        File.WriteAllText(path, BuildTaskXml(ExePath, sid), Encoding.Unicode);
        return path;
    }

    private static string[] CreateArgs(string xmlPath) => ["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"];

    private static string[] DeleteArgs() => ["/Delete", "/TN", TaskName, "/F"];

    private (int ExitCode, string Output) RunSchtasks(string[] args)
    {
        string? xmlFile = args.Length > 4 && args[3] == "/XML" ? args[4] : null;
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return (-1, "");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(SchtasksTimeout))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // আগেই বন্ধ হয়ে গেছে
                }

                Log.Error($"schtasks {args[0]} timed out");
                return (-1, "");
            }

            string output = (stdout.Result + " " + stderr.Result).Trim();
            if (process.ExitCode == 0)
            {
                Log.Info($"schtasks {args[0]}: ok");
            }
            else
            {
                Log.Error($"schtasks {args[0]} exited with {process.ExitCode}: {output}");
            }

            return (process.ExitCode, output);
        }
        finally
        {
            if (xmlFile is not null)
            {
                try
                {
                    File.Delete(xmlFile);
                }
                catch
                {
                    // Temp folder, পরে Windows নিজেই পরিষ্কার করবে
                }
            }
        }
    }
}
