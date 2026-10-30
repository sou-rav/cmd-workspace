using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CmdWorkspace.Core;

/// <summary>
/// Kero style scroll bar for a ScrollViewer: a thin rounded bar with no track and no arrows, floating inside the panel with a gap.
/// It shows when you scroll or point at it, gets wider under the pointer, and fades out after about 1 second idle.
/// Add <see cref="Host"/> to the same grid as the ScrollViewer. The ScrollViewer's own bar is switched off (wheel and touch still scroll).
/// </summary>
public sealed class FloatingScrollbar
{
    private const int IdleMs = 1000, FadeInMs = 80, FadeOutMs = 250;

    private readonly ScrollViewer _sv;
    private readonly Border _thumb = new();
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(IdleMs) };
    private bool _hover, _drag;
    private double _dragStartY, _dragStartOffset, _lastOffset;
    private long _quietUntil;

    /// <summary>The element to place in the panel (right side, full height of the area).</summary>
    public Grid Host { get; } = new();

    public FloatingScrollbar(ScrollViewer sv, double topDip, double bottomDip, double rightDip)
    {
        _sv = sv;
        sv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

        Host.HorizontalAlignment = HorizontalAlignment.Right;
        Host.VerticalAlignment = VerticalAlignment.Stretch;
        Host.Width = Ui.D(18);
        Host.Margin = new Thickness(0, topDip, rightDip, bottomDip);
        Host.Background = Br.Clear;      // a transparent background makes the strip catch the pointer
        Host.Opacity = 0;
        Host.Visibility = Visibility.Collapsed;

        _thumb.Width = Ui.D(4);
        _thumb.HorizontalAlignment = HorizontalAlignment.Right;
        _thumb.VerticalAlignment = VerticalAlignment.Top;
        _thumb.CornerRadius = new CornerRadius(Ui.D(5));
        _thumb.Background = Br.Scroll;
        Host.Children.Add(_thumb);

        sv.ViewChanged += (_, _) => OnViewChanged();
        sv.SizeChanged += (_, _) => Update();
        Host.SizeChanged += (_, _) => Update();
        _idle.Tick += (_, _) => { _idle.Stop(); if (!_hover && !_drag) Anim.Fade(Host, 0, FadeOutMs); };

        Host.PointerEntered += (_, _) => { _hover = true; _thumb.Width = Ui.D(8); Show(); };
        Host.PointerExited += (_, _) => { _hover = false; if (!_drag) _thumb.Width = Ui.D(4); Show(); };
        Host.PointerPressed += OnTrackPressed;
        _thumb.PointerPressed += OnThumbPressed;
        _thumb.PointerMoved += OnThumbMoved;
        _thumb.PointerReleased += (_, e) => EndDrag(e);
        _thumb.PointerCaptureLost += (_, _) => { _drag = false; if (!_hover) _thumb.Width = Ui.D(4); Show(); };
    }

    /// <summary>Do not show the bar for the next short moment (used when a page is redrawn and the scroll spot is restored).</summary>
    public void Quiet() => _quietUntil = Environment.TickCount64 + 500;

    private bool Scrollable => _sv.ExtentHeight > _sv.ViewportHeight + 1;

    private void OnViewChanged()
    {
        Update();
        var moved = Math.Abs(_sv.VerticalOffset - _lastOffset) > 0.5;
        _lastOffset = _sv.VerticalOffset;
        if (moved && Environment.TickCount64 > _quietUntil) Show();
    }

    private void Show()
    {
        if (!Scrollable) return;
        Host.Visibility = Visibility.Visible;
        Anim.Fade(Host, 1, FadeInMs);
        _idle.Stop();
        _idle.Start();
    }

    /// <summary>Recalculates the size and place of the thumb. Safe to call at any time.</summary>
    public void Update()
    {
        if (!Scrollable) { Host.Visibility = Visibility.Collapsed; return; }
        var track = Host.ActualHeight;
        if (track <= 0) return;
        var extent = _sv.ExtentHeight;
        var view = _sv.ViewportHeight;
        var height = Math.Min(track, Math.Max(Ui.D(36), track * view / extent));
        var room = extent - view;
        var top = room <= 0 ? 0 : (track - height) * Math.Clamp(_sv.VerticalOffset / room, 0, 1);
        _thumb.Height = height;
        _thumb.Margin = new Thickness(0, top, Ui.D(3), 0);
        if (Host.Visibility == Visibility.Collapsed) Host.Visibility = Visibility.Visible;   // stays at opacity 0 until Show()
    }

    // ---- dragging the thumb and clicking the strip

    private void OnThumbPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Host);
        if (!point.Properties.IsLeftButtonPressed) return;
        _drag = true;
        _dragStartY = point.Position.Y;
        _dragStartOffset = _sv.VerticalOffset;
        _thumb.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnThumbMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drag) return;
        var travel = Host.ActualHeight - _thumb.ActualHeight;
        if (travel <= 0) return;
        var dy = e.GetCurrentPoint(Host).Position.Y - _dragStartY;
        var room = _sv.ExtentHeight - _sv.ViewportHeight;
        _sv.ChangeView(null, Math.Clamp(_dragStartOffset + dy * room / travel, 0, room), null, true);
        Show();
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        _drag = false;
        _thumb.ReleasePointerCapture(e.Pointer);
        if (!_hover) _thumb.Width = Ui.D(4);
        Show();
    }

    // Click above or below the thumb: one page up or down.
    private void OnTrackPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        var y = e.GetCurrentPoint(Host).Position.Y;
        var top = _thumb.Margin.Top;
        var page = _sv.ViewportHeight * 0.9;
        if (y < top) _sv.ChangeView(null, Math.Max(0, _sv.VerticalOffset - page), null, false);
        else if (y > top + _thumb.ActualHeight) _sv.ChangeView(null, _sv.VerticalOffset + page, null, false);
        Show();
    }
}
