using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace CmdWorkspace.Core;

/// <summary>
/// Shared brushes. Every element uses these same objects, so a theme change only has to set their colours (no rebuild).
/// Must first be touched on the UI thread.
/// </summary>
public static class Br
{
    public static readonly SolidColorBrush Text = new(), Dim = new(), Faint = new(), Mark = new(), Green = new(), Red = new();
    public static readonly SolidColorBrush Card = new(), CardEdge = new(), RowSel = new(), RowSelEdge = new(), Accent = new(), Link = new();
    public static readonly SolidColorBrush Pop = new(), PopEdge = new(), Hover = new(), Divider = new(), SetPane = new(), Group = new();
    public static readonly SolidColorBrush Seg = new(), SegSel = new(), SegSelEdge = new(), SearchBox = new(), RailSel = new();
    public static readonly SolidColorBrush ToggleOn = new(), ToggleOff = new(), Shadow = new(), Scroll = new();
    public static readonly SolidColorBrush Clear = new(Colors.Transparent), White = new(Colors.White);

    public static readonly LinearGradientBrush Window = MakeGradient();
    public static readonly LinearGradientBrush SetWindow = MakeGradient();

    private static LinearGradientBrush MakeGradient()
    {
        var g = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        g.GradientStops.Add(new GradientStop { Offset = 0 });
        g.GradientStops.Add(new GradientStop { Offset = 0.48 });
        g.GradientStops.Add(new GradientStop { Offset = 1 });
        return g;
    }

    private static string? _look;

    /// <summary>Sets all colours. When the theme or the background changed while the app runs, the colours fade (not at start, not when nothing changed).</summary>
    public static void Apply(Palette p, string backgroundId)
    {
        var look = p.IsDark + "|" + backgroundId;
        var animate = _look != null && _look != look;
        _look = look;
        void C(SolidColorBrush brush, Windows.UI.Color color) { if (animate) Anim.ColorTo(brush, color, 180); else brush.Color = color; }
        C(Text, p.Text); C(Dim, p.Dim); C(Faint, p.Faint); C(Mark, p.Mark); C(Green, p.Green); C(Red, p.Red);
        C(Card, p.Card); C(CardEdge, p.CardEdge); C(RowSel, p.RowSel); C(RowSelEdge, p.RowSelEdge);
        C(Accent, p.Accent); C(Link, p.Link); C(Pop, p.Pop); C(PopEdge, p.PopEdge); C(Hover, p.Hover);
        C(Divider, p.Divider); C(SetPane, p.SetPane); C(Group, p.Group); C(Seg, p.Seg); C(SegSel, p.SegSel);
        C(SegSelEdge, p.SegSelEdge); C(SearchBox, p.SearchBox); C(RailSel, p.RailSel);
        C(Scroll, p.IsDark ? Windows.UI.Color.FromArgb(0x66, 255, 255, 255) : Windows.UI.Color.FromArgb(0x4D, 0, 0, 0));   // scroll bar thumb
        C(ToggleOn, p.ToggleOn); C(ToggleOff, p.ToggleOff); C(Shadow, p.Shadow);

        var bg = Palette.Preset(backgroundId, p.IsDark);
        for (var i = 0; i < 3; i++)
        {
            var color = Palette.H(bg[i]);
            if (animate) { Anim.ColorTo(Window.GradientStops[i], color, 180); Anim.ColorTo(SetWindow.GradientStops[i], color, 180); }
            else { Window.GradientStops[i].Color = color; SetWindow.GradientStops[i].Color = color; }   // the Settings window follows the chosen background too
        }
    }
}
