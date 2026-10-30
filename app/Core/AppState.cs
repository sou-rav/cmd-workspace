using System.Text.Json;

namespace CmdWorkspace.Core;

/// <summary>All saved settings. Saved to %LOCALAPPDATA%\CMDWorkspace\settings.json.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public string Theme { get; set; } = "Light";          // Light or Dark. Never follows Windows.
    public string Background { get; set; } = "petal";     // petal, mist, meadow, dusk, plain
    public double TextScale { get; set; } = 1.0;          // 0.9 to 1.3
    public bool BarOpen { get; set; } = true;
    public string StartIn { get; set; } = "Home";         // Home, Current (folder of the active session) or Project (last project used)
    public string NameAfter { get; set; } = "Shell";      // Shell or Folder
    public bool AskBeforeClose { get; set; } = true;
    public string DefaultShell { get; set; } = "cmd";     // cmd, powershell, pwsh
    public double FontSize { get; set; } = 13;            // terminal font size, whole numbers only
    public string FontFamily { get; set; } = "JetBrains Mono"; // a label from Fonts.All
    public double LineHeight { get; set; } = 1.22;          // 1.00 to 2.00
    public string Weight { get; set; } = "Regular";         // Light, Regular, Medium, Semibold
    public bool Ligatures { get; set; } = false;
    public bool Thicken { get; set; } = false;
    public bool CopyOnSelect { get; set; } = false;
    public string RightClick { get; set; } = "Pastes";      // Pastes, Menu, Nothing
    public string CursorStyle { get; set; } = "block";    // block, bar, underline
    public bool CursorBlink { get; set; } = true;
    public bool ShowCopyPaste { get; set; } = true;          // old single switch, only read to carry the choice over
    public bool ShowCopyMenu { get; set; } = true;
    public bool ShowPasteMenu { get; set; } = true;
    public bool ScrollPastEnd { get; set; } = true;          // scroll below the last line into empty space, like Command Prompt (Phase 7, 2026-10-28)
    public bool Animations { get; set; } = true;             // menus, panels and rows move smoothly (Phase 9). Also off when Windows has Animation effects off.
    public bool AskMultiline { get; set; } = true;
    public int Scrollback { get; set; } = 5000;
}

/// <summary>Holds the settings, saves them, and tells every window when something changed.</summary>
public static class AppState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings S { get; private set; } = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMDWorkspace");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                S = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            if (S.StartIn is not ("Home" or "Current" or "Project")) S.StartIn = "Home";
            S.FontSize = Math.Clamp(Math.Round(S.FontSize), 9, 28);
            S.LineHeight = Math.Clamp(S.LineHeight, 1.0, 2.0);
            S.TextScale = Math.Clamp(S.TextScale, 0.9, 1.3);          // Phase 7: a hand-edited file cannot break the layout
            S.Scrollback = Math.Clamp(S.Scrollback, 100, 50000);
            if (!S.ShowCopyPaste) { S.ShowCopyMenu = false; S.ShowPasteMenu = false; S.ShowCopyPaste = true; }   // old switch was off: keep both off
            if (S.Weight is not ("Light" or "Regular" or "Medium" or "Semibold")) S.Weight = "Regular";
            if (S.RightClick is not ("Pastes" or "Menu" or "Nothing")) S.RightClick = "Pastes";   // whole numbers only (older files had 13.2)
        }
        catch (Exception ex)
        {
            Log.Error("settings load (defaults used)", ex);
            S = new AppSettings();
            try { File.Copy(FilePath, Path.Combine(Folder, $"settings.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true); }   // Phase 7: keep the damaged file
            catch { /* nothing to keep */ }
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(S, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    /// <summary>Call after changing a setting: saves and updates every window.</summary>
    public static void Notify()
    {
        Save();
        Changed?.Invoke();
    }

    public static void ResetAll()
    {
        S = new AppSettings();
        Notify();
    }
}
