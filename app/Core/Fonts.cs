using Microsoft.Win32;

namespace CmdWorkspace.Core;

/// <summary>One terminal font choice. Css lists the family names to try, first one first.</summary>
public sealed record TermFont(string Label, string[] Css, string[] Tokens);

/// <summary>The terminal font list. A font is "available" if it is bundled (downloaded by setup-vendor) or installed in Windows.</summary>
public static class Fonts
{
    public const string Default = "JetBrains Mono";

    private static TermFont F(string label, params string[] css) => new(label, css.Length > 0 ? css : new[] { label }, new[] { label.ToLowerInvariant() });

    public static readonly TermFont[] All =
    {
        new("JetBrains Mono Nerd Font", new[] { "JetBrainsMono Nerd Font", "JetBrainsMono NF", "JetBrainsMono Nerd Font Mono" }, new[] { "jetbrainsmono nerd", "jetbrainsmono nf" }),
        new("System", new[] { "Consolas" }, Array.Empty<string>()),
        F("SF Mono", "SF Mono", "SFMono-Regular"),
        F("Menlo"),
        F("Monaco"),
        F("JetBrains Mono"),
        F("Fira Code"),
        F("Cascadia Code"),
        F("Iosevka"),
        F("Berkeley Mono"),
        F("Geist Mono"),
        F("IBM Plex Mono"),
        F("Source Code Pro"),
        F("Hack"),
        F("Maple Mono"),
        F("Cascadia Mono"),
        F("Consolas"),
        F("Courier New"),
        F("Lucida Console"),
        F("MS Gothic"),
        F("NSimSun"),
        F("SimSun-ExtB"),
        F("SimSun-ExtG"),
        F("Ubuntu Mono")
    };

    /// <summary>Normal and bold font weights (CSS numbers) for a Weight setting.</summary>
    public static (int Normal, int Bold) WeightNumbers(string name) => name switch
    {
        "Light" => (300, 600),
        "Medium" => (500, 700),
        "Semibold" => (600, 700),
        _ => (400, 700)
    };

    public static TermFont Get(string label) => All.FirstOrDefault(f => f.Label == label) ?? All.First(f => f.Label == Default);

    /// <summary>CSS font-family text for the terminal: the chosen font first, then safe fallbacks.</summary>
    public static string CssList(string label)
    {
        var f = Get(label);
        return string.Join(", ", f.Css.Select(c => "'" + c + "'").Concat(new[] { "'JetBrains Mono'", "'Cascadia Mono'", "Consolas", "monospace" }));
    }

    private static HashSet<string>? _installed;
    private static string? _bundledCss;

    public static bool IsAvailable(string label)
    {
        var f = Get(label);
        if (f.Tokens.Length == 0) return true;
        _bundledCss ??= ReadBundled();
        if (f.Css.Any(c => _bundledCss.Contains("font-family: '" + c + "'", StringComparison.OrdinalIgnoreCase))) return true;
        _installed ??= ReadInstalled();
        return _installed.Any(name => f.Tokens.Any(t => name.Contains(t)));
    }

    private static string ReadBundled()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Web", "vendor", "fonts", "fonts.css");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch { return ""; }
    }

    private static HashSet<string> ReadInstalled()
    {
        var set = new HashSet<string>();
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key == null) continue;
                foreach (var n in key.GetValueNames()) set.Add(n.ToLowerInvariant());
            }
            catch (Exception ex) { Log.Write("font list: " + ex.Message); }
        }
        return set;
    }
}
