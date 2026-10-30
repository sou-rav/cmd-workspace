namespace CmdWorkspace.Core;

public enum FolderState { Ok, Missing, NoAccess, Unavailable }

/// <summary>
/// Checks whether a folder can be used, without freezing the screen. A disconnected network drive can make
/// Directory.Exists wait for many seconds, so the check runs on a background thread and gives up after a short time.
/// </summary>
public static class Folders
{
    public static async Task<FolderState> CheckAsync(string path, int timeoutMs = 1500)
    {
        var work = Task.Run(() => CheckNow(path));
        var first = await Task.WhenAny(work, Task.Delay(timeoutMs));
        return first == work ? work.Result : FolderState.Unavailable;
    }

    /// <summary>Checks several folders at the same time. Total waiting time is about <paramref name="timeoutMs"/>.</summary>
    public static async Task<Dictionary<string, FolderState>> CheckManyAsync(IEnumerable<string> paths, int timeoutMs = 1000)
    {
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tasks = list.Select(p => CheckAsync(p, timeoutMs)).ToList();
        var states = await Task.WhenAll(tasks);
        var result = new Dictionary<string, FolderState>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++) result[list[i]] = states[i];
        return result;
    }

    private static FolderState CheckNow(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return FolderState.Missing;
            // Listing one entry proves the folder can be opened. An empty folder is fine.
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return FolderState.Ok;
        }
        catch (UnauthorizedAccessException) { return FolderState.NoAccess; }
        catch (DirectoryNotFoundException) { return FolderState.Missing; }
        catch (IOException) { return FolderState.Unavailable; }   // for example a drive that is not ready
        catch (Exception ex)
        {
            Log.Write("folder check failed: " + ex.GetType().Name);
            return FolderState.Unavailable;
        }
    }

    /// <summary>Short tag for lists.</summary>
    public static string Tag(FolderState state) => state switch
    {
        FolderState.Missing => "Missing",
        FolderState.NoAccess => "No access",
        FolderState.Unavailable => "Not available",
        _ => ""
    };

    /// <summary>Plain sentence for notices.</summary>
    public static string Sentence(FolderState state) => state switch
    {
        FolderState.Missing => "The folder was not found. It may have been moved, renamed or deleted.",
        FolderState.NoAccess => "Windows does not allow this account to open the folder.",
        FolderState.Unavailable => "The folder did not answer in time. A drive or network share may be disconnected.",
        _ => ""
    };

    /// <summary>Full path without a trailing backslash (except for a drive root like C:\). Same folder gives the same text.</summary>
    public static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(full) ?? "";
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }
        catch { return path.Trim(); }
    }

    public static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The name shown for a plain folder: "Home" for the user profile, else the last part of the path.</summary>
    public static string DisplayName(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Same(folder, home)) return "Home";
        var name = Path.GetFileName(Normalize(folder));
        return string.IsNullOrEmpty(name) ? folder : name;
    }
}
