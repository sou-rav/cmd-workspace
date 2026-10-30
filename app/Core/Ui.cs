using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CmdWorkspace.Core;

/// <summary>Small helpers to build the screen in code. All sizes come from the mockup, which is in screenshot pixels at 125 percent.</summary>
public static class Ui
{
    /// <summary>Screenshot pixels to DIPs (the user's Kero screenshots were taken at 125 percent).</summary>
    public static double D(double px) => px / 1.25;

    /// <summary>Font size in DIPs from a mockup pixel size. Never below 12 at 100 percent (permanent rule), then the text size setting applies.</summary>
    public static double F(double px) => Math.Max(12.0, px / 1.25) * AppState.S.TextScale;

    public const string MonoFamily = "JetBrains Mono, Cascadia Mono, Consolas";

    public static TextBlock T(string text, double px, Brush fg, bool semi = false, bool bold = false, bool mono = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = F(px),
            Foreground = fg,
            FontWeight = bold ? FontWeights.Bold : semi ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (mono) t.FontFamily = new FontFamily(MonoFamily);
        return t;
    }

    /// <summary>A stroke icon from a 24 x 24 outline. Size is in DIPs.</summary>
    public static FrameworkElement Icon(string outline, double size, Brush stroke, double thickness = 1.7)
    {
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Spaced(outline)),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    // Puts a space around every path command and before every minus sign, so the parser never has to guess.
    private static string Spaced(string d)
    {
        d = Regex.Replace(d, "([A-Za-z])", " $1 ");
        d = d.Replace("-", " -").Replace(",", " ");
        return Regex.Replace(d, @"\s+", " ").Trim();
    }

    public static Button Flat(UIElement content, string name, double radius = 0, double w = double.NaN, double h = double.NaN, Thickness? pad = null)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["FlatButtonStyle"],
            Content = content,
            CornerRadius = new CornerRadius(radius),
            Padding = pad ?? new Thickness(0)
        };
        b.UseSystemFocusVisuals = false;
        b.FocusVisualPrimaryThickness = new Thickness(0);
        b.FocusVisualSecondaryThickness = new Thickness(0);
        if (!double.IsNaN(w)) b.Width = w;
        if (!double.IsNaN(h)) b.Height = h;
        AutomationProperties.SetName(b, name);
        return b;
    }

    /// <summary>A Border that looks like a pop-up (menus, search, notices).</summary>
    public static Border PopBox(UIElement child, double width, double radiusPx = 12, double padPx = 6)
    {
        return new Border
        {
            Width = width,
            Background = Br.Pop,
            BorderBrush = Br.PopEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(D(radiusPx)),
            Padding = new Thickness(D(padPx)),
            Child = child
        };
    }

    public static Border Divider(double marginPx = 8) => new()
    {
        Height = 1,
        Margin = new Thickness(D(marginPx), D(5), D(marginPx), D(5)),
        Background = Br.Divider
    };

    /// <summary>Colours the system window buttons (minimise, maximise, close) for the current theme.</summary>
    public static void ColourCaptionButtons(AppWindow window, bool dark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var p = dark ? Palette.Dark : Palette.Light;
        var bar = window.TitleBar;
        var hover = dark ? Color.FromArgb(0x24, 255, 255, 255) : Color.FromArgb(0x1A, 0, 0, 0);
        var pressed = dark ? Color.FromArgb(0x3A, 255, 255, 255) : Color.FromArgb(0x2C, 0, 0, 0);
        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = p.Text;
        bar.ButtonInactiveForegroundColor = p.Faint;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = p.Text;
        bar.ButtonPressedBackgroundColor = pressed;
        bar.ButtonPressedForegroundColor = p.Text;
    }
}
