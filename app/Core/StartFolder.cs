namespace CmdWorkspace.Core;

/// <summary>
/// Start in a folder (Phase 8c). Typing the app name in the Explorer address bar, Win+R or a command line starts the app with the
/// current folder as its working directory. That folder (or one given with --folder) opens as a NEW session (decision D3).
/// Starts that must not open a folder session: --no-cwd (shortcuts, start.cmd, open-app.cmd), the admin window, and working
/// directories that are the program folder or inside the Windows folder.
/// </summary>
public static class StartFolder
{
    public const string NoCwd = "--no-cwd";

    /// <summary>The folder the app was asked to open, or null. The caller still checks that the folder can be used.</summary>
    public static string? Requested()
    {
        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Any(a => a.Equals("-Embedding", StringComparison.OrdinalIgnoreCase))) return null;   // started by Windows as the default terminal
            if (args.Any(a => a.StartsWith(Elevation.ArgPrefix, StringComparison.OrdinalIgnoreCase))) return null;

            // 1. --folder=PATH or --folder PATH
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--folder=", StringComparison.OrdinalIgnoreCase)) return Clean(a["--folder=".Length..]);
                if (a.Equals("--folder", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return Clean(args[i + 1]);
            }

            // 2. a plain path (typed after the name, spaces allowed without quotes)
            var plain = args.Where(a => !a.StartsWith("-")).ToArray();
            if (plain.Length > 0)
            {
                var joined = Clean(string.Join(" ", plain));
                if (Directory.Exists(joined)) return joined;
                if (Directory.Exists(Clean(plain[0]))) return Clean(plain[0]);
                return joined;   // not found: the caller shows a notice
            }

            // 3. the working directory
            if (args.Any(a => a.Equals(NoCwd, StringComparison.OrdinalIgnoreCase))) return null;
            var cwd = Environment.CurrentDirectory;
            if (string.IsNullOrWhiteSpace(cwd)) return null;
            if (Inside(cwd, AppContext.BaseDirectory)) return null;
            if (Inside(cwd, Environment.GetFolderPath(Environment.SpecialFolder.Windows))) return null;
            return cwd;
        }
        catch (Exception ex)
        {
            Log.Error("start folder", ex);
            return null;
        }
    }

    private static string Clean(string s)
    {
        s = s.Trim().Trim('"').Trim();
        // The right click item sends "path\." so that a drive root such as D:\ does not swallow the closing quote.
        if (s.EndsWith("\\.", StringComparison.Ordinal))
        {
            try { s = Path.GetFullPath(s); } catch { s = s[..^2]; }
        }
        return s;
    }

    private static bool Inside(string path, string parent)
    {
        if (string.IsNullOrEmpty(parent)) return false;
        var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var q = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
        return p.StartsWith(q, StringComparison.OrdinalIgnoreCase);
    }
}
