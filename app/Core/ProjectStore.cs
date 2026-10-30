using System.Text.Json;

namespace CmdWorkspace.Core;

/// <summary>One project = one saved folder with a name.</summary>
public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>A folder the user chose before. Only the folder and the time. Never commands or terminal text.</summary>
public sealed class RecentFolder
{
    public string Path { get; set; } = "";
    public DateTime When { get; set; } = DateTime.Now;
}

public sealed class StoreData
{
    public int Version { get; set; } = 1;
    public List<Project> Projects { get; set; } = new();
    public List<RecentFolder> Recent { get; set; } = new();
    public string? LastProjectId { get; set; }
}

/// <summary>
/// Saved projects, recent folders and the last active project.
/// File: %LOCALAPPDATA%\CMDWorkspace\projects.json (separate from settings.json).
/// Session records are NOT saved (user decision). Call from the UI thread only.
/// </summary>
public static class ProjectStore
{
    public const int MaxRecent = 10;
    public const int MaxNameLength = 40;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static StoreData Data { get; private set; } = new();
    public static string FilePath => Path.Combine(AppState.Folder, "projects.json");

    /// <summary>Set when the saved file could not be read. The main window shows it once.</summary>
    public static string? LoadProblem { get; private set; }

    public static event Action? Changed;
    /// <summary>Raised with a plain sentence when saving fails.</summary>
    public static event Action<string>? Problem;

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var loaded = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath));
            if (loaded == null) throw new InvalidDataException("empty file");
            Data = Clean(loaded);
        }
        catch (Exception ex)
        {
            Log.Error("projects load", ex);
            Data = new StoreData();
            try
            {
                var copy = Path.Combine(AppState.Folder, $"projects.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(FilePath, copy, overwrite: true);
                LoadProblem = "The saved projects file could not be read, so the list starts empty. A copy was kept: " + copy;
            }
            catch
            {
                LoadProblem = "The saved projects file could not be read, so the list starts empty.";
            }
        }
    }

    // Drops broken entries and duplicates, so a hand-edited or damaged file cannot break the screen.
    private static StoreData Clean(StoreData d)
    {
        var clean = new StoreData { LastProjectId = d.LastProjectId };
        foreach (var p in d.Projects ?? new())
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Path)) continue;
            p.Path = Folders.Normalize(p.Path);
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? Folders.DisplayName(p.Path) : Shorten(p.Name);
            if (string.IsNullOrWhiteSpace(p.Id) || clean.Projects.Any(x => x.Id == p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (clean.Projects.Any(x => Folders.Same(x.Path, p.Path))) continue;
            clean.Projects.Add(p);
        }
        foreach (var r in d.Recent ?? new())
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Path)) continue;
            r.Path = Folders.Normalize(r.Path);
            if (clean.Recent.Any(x => Folders.Same(x.Path, r.Path))) continue;
            clean.Recent.Add(r);
        }
        clean.Recent = clean.Recent.OrderByDescending(r => r.When).Take(MaxRecent).ToList();
        if (clean.LastProjectId != null && clean.Projects.All(p => p.Id != clean.LastProjectId)) clean.LastProjectId = null;
        return clean;
    }

    private static string Shorten(string name)
    {
        name = name.Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppState.Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Data, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("projects save", ex);
            Problem?.Invoke("Your projects and recent folders could not be saved: " + ex.Message);
        }
    }

    private static void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- projects

    public static Project? ById(string? id) => id == null ? null : Data.Projects.FirstOrDefault(p => p.Id == id);

    public static Project? ByPath(string folder) => Data.Projects.FirstOrDefault(p => Folders.Same(p.Path, folder));

    /// <summary>Adds a project for a folder. Returns null with the reason when it is not allowed.</summary>
    public static Project? Add(string folder, string? name, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(folder)) { problem = "No folder was given."; return null; }
        var path = Folders.Normalize(folder);
        var existing = ByPath(path);
        Log.Write("project add: folder=" + path + " saved=" + Data.Projects.Count + " duplicate=" + (existing != null));
        if (existing != null) { problem = "This folder is already saved as the project \"" + existing.Name + "\"."; return null; }
        var project = new Project
        {
            Path = path,
            Name = string.IsNullOrWhiteSpace(name) ? Shorten(Folders.DisplayName(path)) : Shorten(name)
        };
        Data.Projects.Add(project);
        Commit();
        return project;
    }

    public static void Rename(string id, string name)
    {
        var p = ById(id);
        name = Shorten(name);
        if (p == null || name.Length == 0 || p.Name == name) return;
        p.Name = name;
        Commit();
    }

    /// <summary>Removes only the saved entry. The folder on disk is never touched.</summary>
    public static void Remove(string id)
    {
        var p = ById(id);
        if (p == null) return;
        Data.Projects.Remove(p);
        if (Data.LastProjectId == id) Data.LastProjectId = null;
        Commit();
    }

    public static void SetLastProject(string? id)
    {
        if (Data.LastProjectId == id) return;
        Data.LastProjectId = id;
        Save();   // no screen needs to update for this
    }

    // ---------------------------------------------------------------- recent folders

    public static void RememberRecent(string folder)
    {
        var path = Folders.Normalize(folder);
        Data.Recent.RemoveAll(r => Folders.Same(r.Path, path));
        Data.Recent.Insert(0, new RecentFolder { Path = path, When = DateTime.Now });
        if (Data.Recent.Count > MaxRecent) Data.Recent.RemoveRange(MaxRecent, Data.Recent.Count - MaxRecent);
        Commit();
    }

    public static void RemoveRecent(string folder)
    {
        if (Data.Recent.RemoveAll(r => Folders.Same(r.Path, folder)) > 0) Commit();
    }

    public static void ClearRecent()
    {
        if (Data.Recent.Count == 0) return;
        Data.Recent.Clear();
        Commit();
    }
}
