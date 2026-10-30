using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace CmdWorkspace.Core;

/// <summary>Administrator mode. A shell inside the app is admin only if the app itself runs as admin, so "Open as Administrator" starts a second app window.</summary>
public static class Elevation
{
    public const string ArgPrefix = "--admin-shell=";

    public static bool IsElevated
    {
        get
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    /// <summary>The shell id given on the command line by an elevated start, or null.</summary>
    public static string? RequestedShell()
    {
        foreach (var a in Environment.GetCommandLineArgs())
            if (a.StartsWith(ArgPrefix, StringComparison.OrdinalIgnoreCase)) return a[ArgPrefix.Length..];
        return null;
    }

    /// <summary>Starts a new app window as administrator (Windows shows its Yes/No prompt). Returns a plain sentence when it did not start, null when it did.</summary>
    public static string? LaunchAsAdmin(string shellId)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return "The app file could not be found.";
            Process.Start(new ProcessStartInfo(exe, ArgPrefix + shellId) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            Log.Write("admin window requested: " + shellId);
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Cancelled. Nothing was started."; }
        catch (Exception ex) { Log.Error("admin launch", ex); return ex.Message; }
    }
}
