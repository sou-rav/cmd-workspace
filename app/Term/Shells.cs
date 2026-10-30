using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CmdWorkspace.Core;

namespace CmdWorkspace.Term;

/// <summary>One shell this app can start.</summary>
public sealed record ShellInfo(string Id, string MenuName, string ShortName, string Exe, string Args)
{
    public bool IsInstalled => File.Exists(Exe);
}

/// <summary>Finds the shells on this PC. PowerShell 7 counts as installed only if its file really exists.</summary>
public static class Shells
{
    public static readonly ShellInfo Cmd =
        new("cmd", "Command Prompt", "CMD", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/K cls");

    public static readonly ShellInfo WindowsPowerShell =
        new("powershell", "Windows PowerShell", "PowerShell", Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), "-NoLogo");

    public static readonly ShellInfo PowerShell7 =
        new("pwsh", "PowerShell 7", "PowerShell 7", FindPwsh(), "-NoLogo");

    private static readonly ShellInfo[] BuiltIn = { Cmd, WindowsPowerShell, PowerShell7 };

    private static IReadOnlyList<ShellInfo> _wsl = Array.Empty<ShellInfo>();
    private static DateTime _wslChecked = DateTime.MinValue;

    /// <summary>The built-in shells, then the Linux distributions Windows lists for WSL (found by itself, nothing to set up).</summary>
    public static IEnumerable<ShellInfo> All => BuiltIn.Concat(_wsl);

    /// <summary>
    /// Asks Windows which WSL distributions are installed (wsl.exe --list --quiet). Runs in the background, gives up after
    /// about 2.5 seconds and keeps the old list then. A fresh answer is reused for 30 seconds unless <paramref name="force"/> is set.
    /// If WSL is not installed the list is empty and nothing is shown.
    /// </summary>
    public static async Task RefreshWslAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _wslChecked < TimeSpan.FromSeconds(30)) return;
        var work = Task.Run(ReadWsl);
        var first = await Task.WhenAny(work, Task.Delay(2500));
        if (first != work)
        {
            Log.Write("wsl list did not answer in time, old list kept");
            _wslChecked = DateTime.UtcNow - TimeSpan.FromSeconds(20);   // try again in about 10 seconds
            return;
        }
        _wsl = work.Result;
        _wslChecked = DateTime.UtcNow;
        Log.Write("wsl distributions found: " + _wsl.Count);
    }

    private static IReadOnlyList<ShellInfo> ReadWsl()
    {
        var list = new List<ShellInfo>();
        try
        {
            var wslExe = Path.Combine(Environment.SystemDirectory, "wsl.exe");
            if (!File.Exists(wslExe)) return list;
            var info = new ProcessStartInfo(wslExe, "--list --quiet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            using var p = Process.Start(info);
            if (p == null) return list;
            // Phase 7: an old wsl.exe stub asks "Press any key to install WSL" (seen in the user's logs). Closing its input ends that wait at once.
            try { p.StandardInput.Close(); } catch { /* already ended */ }
            using var bytes = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(bytes);
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(2500)) { try { p.Kill(true); } catch { } return _wsl; }
            copy.Wait(500);
            if (p.ExitCode != 0) return list;   // WSL not set up, or no distribution installed

            // wsl.exe writes UTF-16 (NUL bytes between letters) on most versions, UTF-8 on some.
            var raw = bytes.ToArray();
            var text = raw.Contains((byte)0) ? Encoding.Unicode.GetString(raw) : Encoding.UTF8.GetString(raw);
            foreach (var line in text.Split('\n'))
            {
                var name = line.Replace("\uFEFF", "").Replace("\r", "").Replace("\0", "").Trim();
                if (name.EndsWith("(Default)", StringComparison.OrdinalIgnoreCase)) name = name[..^"(Default)".Length].Trim();
                if (name.Length == 0 || name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) continue;
                // Phase 7: when no distribution exists, wsl.exe can print a sentence ("Windows Subsystem for Linux has no installed distributions.")
                // that looks like a name. It is not one.
                if (name.StartsWith("Windows Subsystem", StringComparison.OrdinalIgnoreCase) || name.Contains("distribution", StringComparison.OrdinalIgnoreCase)) { Log.Write("wsl message line skipped"); continue; }
                // Only plain names. This text ends up in a command line, so anything unusual is skipped.
                if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._ -]{0,63}$")) { Log.Write("wsl name skipped (unusual characters)"); continue; }
                // Phase 7: no quotes when the name has no space. In the user's test "wsl -d Ubuntu" worked in CMD, but the quoted form from this app said the distribution was not found.
                list.Add(new ShellInfo("wsl:" + name, name + " (WSL)", name, wslExe, name.Contains(' ') ? "-d \"" + name + "\"" : "-d " + name));
            }
        }
        catch (Exception ex) { Log.Error("wsl list", ex); }
        Log.Write("wsl names: " + (list.Count == 0 ? "(none)" : string.Join(", ", list.Select(s => s.ShortName))));   // distribution names only
        return list;
    }

    public static ShellInfo ById(string id) => All.FirstOrDefault(s => s.Id == id && s.IsInstalled) ?? Cmd;

    private static string FindPwsh()
    {
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe");
        if (File.Exists(standard)) return standard;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* bad PATH entry */ }
        }
        return standard;
    }
}
