using System.Diagnostics;
using System.Text;

namespace Driftwall.Core;

/// <summary>
/// Minimal append-only logger. Writes are batched behind a lock and the file is capped, so logging
/// never becomes a background cost. Debug builds also mirror to the debugger output.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;

    private static readonly Lock Gate = new();
    private static string? _path;
    private static bool _failed;

    public static string LogPath => _path ??= Path.Combine(Paths.DataDirectory, "driftwall.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level).Append("] ")
            .Append(message);

        if (ex is not null) line.Append(" :: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

        var text = line.ToString();
        Debug.WriteLine(text);

        if (_failed) return;

        lock (Gate)
        {
            try
            {
                var path = LogPath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) Rotate(path);

                File.AppendAllText(path, text + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // A logger that throws is worse than no logger. Give up quietly and permanently.
                _failed = true;
            }
        }
    }

    private static void Rotate(string path)
    {
        var backup = path + ".1";
        try
        {
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(path, backup);
        }
        catch
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
