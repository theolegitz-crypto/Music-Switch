using System.IO;
using System.Text;

namespace MediaController.Core;

/// <summary>
/// Minimal append-only file logger: %AppData%\MediaController\logs\app.log.
/// ponytail: no logging framework, no levels config, no async sink. One lock and a size cap.
/// </summary>
public static class Logger
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static readonly string LogFile;

    static Logger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MediaController",
            "logs");

        LogFile = Path.Combine(dir, "app.log");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Logging must never be the reason the app fails to start.
        }
    }

    public static string FilePath => LogFile;

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(LogFile, LogFile + ".old", overwrite: true);
                }

                File.AppendAllText(
                    LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Swallowed on purpose: a failing log write must not break media control.
        }
    }
}
