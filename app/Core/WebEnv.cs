namespace CmdWorkspace.Core;

/// <summary>
/// WebView2 data folder (Phase 8a). Set through the documented environment variable WEBVIEW2_USER_DATA_FOLDER before any web view starts,
/// so the terminal page and the Settings preview both use it and the app also works when installed in Program Files.
/// Normal window: %LOCALAPPDATA%\CMDWorkspace\WebView2. Administrator window: WebView2-admin (never shared with the normal window).
/// </summary>
public static class WebEnv
{
    public static string DataFolder { get; } =
        Path.Combine(AppState.Folder, Elevation.IsElevated ? "WebView2-admin" : "WebView2");

    /// <summary>Call once at app start, before the first window is created.</summary>
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", DataFolder);
            Log.Write("WebView2 data folder: " + DataFolder);
        }
        catch (Exception ex)
        {
            // Without the variable the default (next to the exe) is used, which fails in Program Files.
            Log.Error("WebView2 data folder (" + DataFolder + ")", ex);
        }
    }
}
