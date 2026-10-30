namespace CmdWorkspace.Core;

/// <summary>
/// Small log file. start.cmd sets CMDWS_LOGDIR so the logs land in the project folder (logs\app.log, logs\crash.log).
/// Never writes terminal text or clipboard text.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Dir { get; } = FindDir();

    private static string FindDir()
    {
        var env = Environment.GetEnvironmentVariable("CMDWS_LOGDIR");
        // Started from the built "program" folder without start.cmd: use the project's logs folder next to it.
        var beside = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));
        var dir = !string.IsNullOrWhiteSpace(env)
            ? env
            : Directory.Exists(beside) ? beside
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMDWorkspace", "logs");
        try { Directory.CreateDirectory(dir); } catch { /* the log is optional */ }
        // Phase 7: a log bigger than 2 MB is moved aside once per start (the old one is replaced), so logs cannot grow forever.
        foreach (var name in new[] { "app", "crash" })
        {
            try
            {
                var file = Path.Combine(dir, name + ".log");
                if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024) File.Move(file, Path.Combine(dir, name + ".old.log"), overwrite: true);
            }
            catch { /* the log is optional */ }
        }
        return dir;
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path.Combine(Dir, "app.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* never throw from the logger */ }
    }

    public static void Error(string where, Exception ex) => Write($"ERROR in {where}: {ex}");
}
