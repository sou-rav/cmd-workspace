using Windows.UI;

namespace CmdWorkspace.Core;

/// <summary>
/// Colours of the Kero design (design/KERO_REDESIGN.md section 3 and design/kero-mockup/Window.dc.html).
/// Values marked "mockup" come straight from the approved mockup script. Everything else follows from them.
/// </summary>
public sealed class Palette
{
    public bool IsDark { get; init; }
    public Color Text, Dim, Faint, Mark, Green, Red, Card, CardEdge, RowSel, RowSelEdge, Accent, Link;
    public Color Pop, PopEdge, Hover, Divider, SetPane, Group, Seg, SegSel, SegSelEdge, SearchBox, RailSel;
    public Color ToggleOn, ToggleOff, TermSel, Cursor, Shadow, SetBg0, SetBg1, SetBg2;
    /// <summary>The 16 terminal colours: black, red, green, yellow, blue, magenta, cyan, white, then the bright ones.</summary>
    public string[] Ansi = Array.Empty<string>();

    public static Color H(string hex)
    {
        var h = hex.TrimStart('#');
        return Color.FromArgb(255, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    public static Color A(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    public static readonly Palette Light = new()
    {
        IsDark = false,
        Text = H("#1d1d21"), Dim = H("#7f7c88"), Faint = H("#a09da8"), Mark = H("#8a8892"), Green = H("#2fb457"), Red = H("#d94848"),
        Card = H("#ffffff"), CardEdge = A(0, 0, 0, 0), RowSel = H("#ffffff"), RowSelEdge = A(0x24, 70, 50, 100),
        Accent = H("#2f6bf0"), Link = H("#2457d6"),
        Pop = H("#ffffff"), PopEdge = A(0x12, 0, 0, 0), Hover = A(0x12, 60, 40, 100), Divider = A(0x14, 0, 0, 0),
        SetPane = H("#fdfcfd"), Group = H("#f4f4f4"), Seg = H("#ebebeb"), SegSel = H("#ffffff"), SegSelEdge = A(0x24, 0, 0, 0),
        SearchBox = H("#ffffff"), RailSel = H("#ffffff"), ToggleOn = H("#2f6bf0"), ToggleOff = H("#c9c9cf"),
        TermSel = A(0x3D, 60, 110, 240), Cursor = H("#1d1d21"), Shadow = A(5, 80, 60, 110),
        SetBg0 = H("#efe3f6"), SetBg1 = H("#f5eaed"), SetBg2 = H("#f6efd8"),
        // Contrast-safe set for a white card (user choice, 2026-10-24). Bright black and bright yellow (the PowerShell typed command and argument colours) were changed on 2026-10-26 to the values sampled from the user's Kero screenshot (#bb8d14, #8e8e93). They are lighter than the safe set, so contrast on white is lower (user request, reference wins).
        Ansi = new[]
        {
            "#1d1d21", "#c62828", "#1b7f3a", "#8a6100", "#2457d6", "#8e3bb0", "#0b7285", "#6b6874",
            "#8e8e93", "#e0302f", "#1e8e3e", "#bb8d14", "#2f6bf0", "#a24bd0", "#0e8aa3", "#4a4853"
        }
    };

    public static readonly Palette Dark = new()
    {
        IsDark = true,
        Text = H("#e8e8ec"), Dim = H("#9a98a3"), Faint = H("#7a7884"), Mark = H("#a9a7b2"), Green = H("#4cc472"), Red = H("#ef6b6b"),
        Card = H("#232328"), CardEdge = A(0x0D, 255, 255, 255), RowSel = H("#2f2d37"), RowSelEdge = A(0x12, 255, 255, 255),
        Accent = H("#7aa2ff"), Link = H("#7aa2ff"),
        Pop = H("#2a2830"), PopEdge = A(0x17, 255, 255, 255), Hover = A(0x12, 255, 255, 255), Divider = A(0x17, 255, 255, 255),
        SetPane = H("#232328"), Group = H("#2c2c32"), Seg = H("#1f1f24"), SegSel = H("#3b3b44"), SegSelEdge = A(0, 0, 0, 0),
        SearchBox = H("#2c2c32"), RailSel = H("#2f2d37"), ToggleOn = H("#3d78f5"), ToggleOff = H("#4a4a52"),
        TermSel = A(0x4D, 110, 150, 255), Cursor = H("#e8e8ec"), Shadow = A(0, 0, 0, 0),
        SetBg0 = H("#1d1926"), SetBg1 = H("#1e1b1d"), SetBg2 = H("#1e1b1d"),
        Ansi = new[]
        {
            "#4a4a55", "#ff7b72", "#7ee787", "#e3b341", "#7aa2ff", "#d2a8ff", "#56d4dd", "#c9c9d1",
            "#7a7884", "#ffa198", "#9be9a8", "#f2cc60", "#a9c3ff", "#e2c5ff", "#8be9f0", "#ffffff"
        }
    };

    /// <summary>Background presets of the Appearance page (mockup). Three gradient colours, top to bottom.
    /// Petal is the colour sampled from the user's Kero screenshots (KERO_REDESIGN section 2), the others are from the mockup.</summary>
    public static readonly string[] PresetIds = { "petal", "mist", "meadow", "dusk", "plain" };
    public static readonly string[] PresetNames = { "Petal", "Mist", "Meadow", "Dusk", "Plain" };

    private static readonly Dictionary<string, string[]> LightPresets = new()
    {
        ["petal"] = new[] { "#ece1f3", "#f4e9ef", "#f6efd4" },
        ["mist"] = new[] { "#e6eaf3", "#f1f1f3", "#f1f0ee" },
        ["meadow"] = new[] { "#e8efe7", "#f2f3e3", "#f3f2dd" },
        ["dusk"] = new[] { "#e6daee", "#f5e2dc", "#f2e2d2" },
        ["plain"] = new[] { "#edecf2", "#ededf5", "#edeef2" },
    };

    private static readonly Dictionary<string, string[]> DarkPresets = new()
    {
        ["petal"] = new[] { "#18151f", "#1a1719", "#1b1918" },
        ["mist"] = new[] { "#17161c", "#17181c", "#1b1a1f" },
        ["meadow"] = new[] { "#121615", "#151a16", "#1a1a18" },
        ["dusk"] = new[] { "#17111d", "#1e181c", "#1e181a" },
        ["plain"] = new[] { "#121116", "#141318", "#141318" },
    };

    public static string[] Preset(string id, bool dark)
    {
        var map = dark ? DarkPresets : LightPresets;
        return map.TryGetValue(id, out var colours) ? colours : map["petal"];
    }

    public static string Css(Color c) => $"rgba({c.R},{c.G},{c.B},{(c.A / 255.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
}
