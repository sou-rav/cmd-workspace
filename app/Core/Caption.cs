using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CmdWorkspace.Core;

/// <summary>
/// Our own window buttons (Kero look: small rounded tiles, red for Close) and window dragging.
/// The system title bar is switched off; border, resize and rounded corners stay.
/// Not built in: the Windows 11 snap layout flyout on the maximize button.
/// </summary>
public static class Caption
{
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    private const uint WM_CLOSE = 0x0010;

    private const string MinPath = "M5 12H19";
    private const string MaxPath = "M7 5H17A2 2 0 0 1 19 7V17A2 2 0 0 1 17 19H7A2 2 0 0 1 5 17V7A2 2 0 0 1 7 5Z";
    private const string RestorePath = "M9 8V7A2 2 0 0 1 11 5H17A2 2 0 0 1 19 7V13A2 2 0 0 1 17 15H16 M7 8H13A2 2 0 0 1 15 10V17A2 2 0 0 1 13 19H7A2 2 0 0 1 5 17V10A2 2 0 0 1 7 8Z";

    private static readonly SolidColorBrush CloseRed = new(Color.FromArgb(255, 202, 40, 54));
    private static readonly SolidColorBrush White = new(Color.FromArgb(255, 255, 255, 255));

    /// <summary>Removes the system title bar and its buttons. Call before ResizeClient.</summary>
    public static void RemoveSystemBar(Window w)
    {
        if (w.AppWindow.Presenter is OverlappedPresenter p) p.SetBorderAndTitleBar(true, false);
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    /// <summary>Dragging by pressing on the area (we move the window ourselves). Double click toggles maximize when allowed.</summary>
    public static void MakeDraggable(Window w, UIElement area, bool canMaximize)
    {
        long lastTick = 0;
        double lastX = 0, lastY = 0;
        var dragging = false;
        POINT startCursor = default;
        Windows.Graphics.PointInt32 startPos = default;

        area.PointerPressed += (s, e) =>
        {
            var pt = e.GetCurrentPoint((UIElement)s);
            if (!pt.Properties.IsLeftButtonPressed) return;
            var now = Environment.TickCount64;
            if (canMaximize && lastTick != 0 && now - lastTick <= GetDoubleClickTime()
                && Math.Abs(pt.Position.X - lastX) < 6 && Math.Abs(pt.Position.Y - lastY) < 6)
            {
                lastTick = 0;
                ToggleMaximize(w);
                e.Handled = true;
                return;
            }
            lastTick = now;
            lastX = pt.Position.X;
            lastY = pt.Position.Y;
            if (w.AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized) return;
            GetCursorPos(out startCursor);
            startPos = w.AppWindow.Position;
            dragging = ((UIElement)s).CapturePointer(e.Pointer);
            e.Handled = true;
        };
        area.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            if (!e.GetCurrentPoint((UIElement)s).Properties.IsLeftButtonPressed) { dragging = false; ((UIElement)s).ReleasePointerCaptures(); return; }
            GetCursorPos(out var now);
            w.AppWindow.Move(new Windows.Graphics.PointInt32(startPos.X + now.X - startCursor.X, startPos.Y + now.Y - startCursor.Y));
        };
        void Stop(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            ((UIElement)s).ReleasePointerCaptures();
        }
        area.PointerReleased += Stop;
        area.PointerCanceled += Stop;
        area.PointerCaptureLost += (s, e) => dragging = false;
    }

    private static void ToggleMaximize(Window w)
    {
        if (w.AppWindow.Presenter is not OverlappedPresenter p) return;
        if (p.State == OverlappedPresenterState.Maximized) p.Restore(); else p.Maximize();
    }

    /// <summary>Minimize, (maximize), close. Put it in the window's top right corner. topDip is the gap above the tiles.</summary>
    public static StackPanel Buttons(Window w, bool canMaximize, double topDip, bool canMinimize = true)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Ui.D(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, topDip, Ui.D(22), 0)
        };

        var size = Ui.D(17);
        if (canMinimize)
            panel.Children.Add(Tile("Minimize", Ui.Icon(MinPath, size, Br.Dim, 1.6), null, Br.Hover,
                () => { if (w.AppWindow.Presenter is OverlappedPresenter p) p.Minimize(); }));

        if (canMaximize)
        {
            var maxIcon = Ui.Icon(MaxPath, size, Br.Dim, 1.6);
            var restoreIcon = Ui.Icon(RestorePath, size, Br.Dim, 1.6);
            restoreIcon.Visibility = Visibility.Collapsed;
            var holder = new Grid();
            holder.Children.Add(maxIcon);
            holder.Children.Add(restoreIcon);
            w.AppWindow.Changed += (_, args) =>
            {
                if (!args.DidSizeChange) return;
                var maximized = w.AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized;
                maxIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
                restoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
            };
            panel.Children.Add(Tile("Maximize", holder, null, Br.Hover, () => ToggleMaximize(w)));
        }

        // Closing goes through the normal close message, so "ask before closing" still applies.
        panel.Children.Add(Tile("Close", Ui.Icon(Icons.Close, size, Br.Dim, 1.6), Ui.Icon(Icons.Close, size, White, 1.6), CloseRed,
            () => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)));
        return panel;
    }

    private static Border Tile(string tip, UIElement icon, UIElement? hoverIcon, Brush hoverBackground, Action click)
    {
        var holder = new Grid();
        holder.Children.Add(icon);
        if (hoverIcon != null)
        {
            hoverIcon.Visibility = Visibility.Collapsed;
            holder.Children.Add(hoverIcon);
        }
        var fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        var tile = new Border
        {
            Width = Ui.D(35),
            Height = Ui.D(35),
            CornerRadius = new CornerRadius(Ui.D(9)),
            Background = fill,
            Child = holder
        };
        tile.PointerEntered += (_, _) =>
        {
            Anim.ColorTo(fill, ((SolidColorBrush)hoverBackground).Color, 110);
            if (hoverIcon != null) { icon.Visibility = Visibility.Collapsed; hoverIcon.Visibility = Visibility.Visible; }
        };
        tile.PointerExited += (_, _) =>
        {
            var hc = ((SolidColorBrush)hoverBackground).Color;
            Anim.ColorTo(fill, Color.FromArgb(0, hc.R, hc.G, hc.B), 140);
            if (hoverIcon != null) { icon.Visibility = Visibility.Visible; hoverIcon.Visibility = Visibility.Collapsed; }
        };
        tile.Tapped += (_, _) => click();
        ToolTipService.SetToolTip(tile, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, tip);
        return tile;
    }
}
