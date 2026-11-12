using System.IO;
using System.Text;

namespace PulseBar.Utilities;

/// <summary>
/// ছোট file log: %LocalAppData%\PulseBar\logs\pulsebar.log। ১ MB পেরোলে পুরনোটা .old হয়ে যায়।
/// যেকোনো thread থেকে ডাকা যায়, আর লিখতে না পারলেও কখনো exception ছোড়ে না।
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseBar",
        "logs");

    public static string FilePath { get; } = Path.Combine(Directory, "pulsebar.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ').Append(level)
            .Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        line.AppendLine();

        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, line.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Log লিখতে না পারলে app থামবে না
            }
        }
    }
}
