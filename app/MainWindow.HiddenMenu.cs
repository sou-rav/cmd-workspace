using CmdWorkspace.Core;
using CmdWorkspace.Term;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CmdWorkspace;

// Phase 9a: while the left bar is hidden, resting the pointer on the bar icon drops a small icon strip
// (Sessions, Search, Settings). Pointing at Sessions opens the session list beside it.
// Design: design/collapsed-menu/mockup.html. Opens after 600 ms so a quick move to click the icon never opens it.
public sealed partial class MainWindow
{
    private const int HiddenOpenMs = 600;     // rest time on the icon before the strip opens
    private const int HiddenCloseMs = 250;    // grace time after the pointer left the icon, the strip and the list
    private const int FlyoutMs = 100;         // rest time on the Sessions icon before the list opens

    private Popup? _hmStrip, _hmFly;
    private FrameworkElement? _hmStripBox, _hmFlyBox;
    private bool _overToggle, _overStrip, _overFly, _flyWanted;
    private DispatcherTimer? _hmOpenTimer, _hmCloseTimer, _hmFlyTimer;

    private static DispatcherTimer MakeTimer(int ms, Action tick)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    private bool AnyHiddenOver => _overToggle || _overStrip || _overFly;

    // Called once from BuildChrome with the bar toggle button.
    private void WireHiddenMenu(Button toggle)
    {
        _hmOpenTimer = MakeTimer(HiddenOpenMs, OpenHiddenMenu);
        _hmCloseTimer = MakeTimer(HiddenCloseMs, () => { if (!AnyHiddenOver) CloseHiddenMenu(false); });
        _hmFlyTimer = MakeTimer(FlyoutMs, () => { if (_flyWanted) ShowFlyout(); else HideFlyout(false); });

        toggle.PointerEntered += (_, _) =>
        {
            _overToggle = true;
            _hmCloseTimer.Stop();
            if (!AppState.S.BarOpen && _hmStrip == null) { _hmOpenTimer.Stop(); _hmOpenTimer.Start(); }
        };
        toggle.PointerExited += (_, _) => { _overToggle = false; _hmOpenTimer.Stop(); ScheduleHiddenClose(); };
        // A click shows the bar (the toggle's own click handler). A waiting open is cancelled at once.
        toggle.Click += (_, _) => { _hmOpenTimer.Stop(); CloseHiddenMenu(true); };
        // Keyboard: focusing the icon with Tab opens the strip at once, Escape closes it.
        toggle.GotFocus += (_, _) => { if (toggle.FocusState == FocusState.Keyboard) OpenHiddenMenu(); };
        toggle.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { CloseHiddenMenu(false); e.Handled = true; } };
    }

    private void ScheduleHiddenClose()
    {
        if (_hmStrip == null) return;
        _hmCloseTimer?.Stop();
        _hmCloseTimer?.Start();
    }

    private Button StripButton(string icon, string name, Action click, int sessionCount = -1)
    {
        FrameworkElement content = Ui.Icon(icon, Ui.D(24), Br.Dim);
        if (sessionCount > 0)
        {
            var grid = new Grid { Width = Ui.D(30), Height = Ui.D(30) };
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(content);
            var badge = new Border
            {
                MinWidth = Ui.D(16), Height = Ui.D(16), CornerRadius = new CornerRadius(Ui.D(8)), Background = Br.Accent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -Ui.D(3), -Ui.D(5), 0), Padding = new Thickness(Ui.D(4), 0, Ui.D(4), 0),
                Child = new TextBlock
                {
                    Text = sessionCount.ToString(), FontSize = Ui.F(11), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            grid.Children.Add(badge);
            content = grid;
        }
        var b = Ui.Flat(content, name, Ui.D(10), Ui.D(42), Ui.D(42));
        b.Click += (_, _) => click();
        b.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { CloseHiddenMenu(false); e.Handled = true; } };
        ToolTipService.SetToolTip(b, name);
        return b;
    }

    private void OpenHiddenMenu()
    {
        if (AppState.S.BarOpen || _hmStrip != null || _settingsOpen) return;
        _hmCloseTimer?.Stop();

        var stack = new StackPanel { Spacing = Ui.D(2) };
        var count = _sessions.Sessions.Count;
        var sess = StripButton(Icons.Sessions, "Sessions", () => { _flyWanted = true; if (_hmFly == null) ShowFlyout(); else HideFlyout(false); }, count);
        var search = StripButton(Icons.Search, "Search", () => { CloseHiddenMenu(true); ShowSearch(); });
        var settings = StripButton(Icons.Gear, "Settings", () => { CloseHiddenMenu(true); OpenSettings(); });
        sess.PointerEntered += (_, _) => { _flyWanted = true; _hmFlyTimer?.Stop(); _hmFlyTimer?.Start(); };
        search.PointerEntered += (_, _) => { _flyWanted = false; _hmFlyTimer?.Stop(); _hmFlyTimer?.Start(); };
        settings.PointerEntered += (_, _) => { _flyWanted = false; _hmFlyTimer?.Stop(); _hmFlyTimer?.Start(); };
        stack.Children.Add(sess);
        stack.Children.Add(search);
        stack.Children.Add(settings);

        var box = Ui.PopBox(stack, Ui.D(58), 14, 6);
        box.PointerEntered += (_, _) => { _overStrip = true; _hmCloseTimer?.Stop(); };
        box.PointerExited += (_, _) => { _overStrip = false; ScheduleHiddenClose(); };

        var x = 15.6 + Ui.D(37) / 2 - Ui.D(58) / 2;
        _hmStripBox = box;
        _hmStrip = Open(box, x, Ui.D(58), lightDismiss: false, slide: -8);
        // The icons appear one after another.
        for (var i = 0; i < stack.Children.Count; i++)
            if (stack.Children[i] is FrameworkElement e) Anim.PopIn(e, 0, -5, 190, 30 * i);
    }

    private void ShowFlyout()
    {
        if (_hmFly != null || _hmStrip == null) return;
        var list = new StackPanel();
        var sessions = _sessions.Sessions;
        if (sessions.Count == 0)
        {
            var none = Ui.T("No sessions yet", 15, Br.Faint);
            none.Margin = new Thickness(Ui.D(12), Ui.D(10), Ui.D(12), Ui.D(10));
            list.Children.Add(none);
        }
        foreach (var group in sessions.GroupBy(s => GroupOf(s).Key))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.D(8), Margin = new Thickness(Ui.D(12), Ui.D(6), 0, Ui.D(3)) };
            header.Children.Add(Ui.T(GroupOf(group.First()).Name, 14, Br.Dim, semi: true));
            header.Children.Add(Ui.T(group.Count().ToString(), 14, Br.Faint));
            list.Children.Add(header);
            foreach (var s in group) list.Children.Add(FlyRow(s));
        }
        var scroll = new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(Ui.D(120), Math.Min(Ui.D(560), Root.ActualHeight - Ui.D(120)))
        };
        var box = Ui.PopBox(scroll, Ui.D(310), 14, 6);
        box.PointerEntered += (_, _) => { _overFly = true; _flyWanted = true; _hmFlyTimer?.Stop(); _hmCloseTimer?.Stop(); };
        box.PointerExited += (_, _) => { _overFly = false; ScheduleHiddenClose(); };
        box.RequestedTheme = Root.RequestedTheme;

        var stripX = 15.6 + Ui.D(37) / 2 - Ui.D(58) / 2;
        var popup = new Popup
        {
            XamlRoot = Root.XamlRoot, Child = box, IsLightDismissEnabled = false,
            HorizontalOffset = stripX + Ui.D(58) + Ui.D(6), VerticalOffset = Ui.D(58)
        };
        popup.IsOpen = true;
        _hmFly = popup;
        _hmFlyBox = box;
        Anim.PopIn(box, -10, 0, 180);
    }

    private UIElement FlyRow(Session session)
    {
        var selected = session == _active;
        var name = RowName(session);
        var content = new Grid { Margin = new Thickness(Ui.D(10), 0, Ui.D(8), 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Ui.T(name, 15, Br.Text);
        var time = Ui.T(Ago(session.Started), 14, Br.Faint);
        time.Margin = new Thickness(0, 0, Ui.D(10), 0);
        Grid.SetColumn(time, 1);
        var mark = new Grid { Width = Ui.D(18), Height = Ui.D(18) };
        mark.Children.Add(MarkFor(session));
        Grid.SetColumn(mark, 2);
        content.Children.Add(label);
        content.Children.Add(time);
        content.Children.Add(mark);
        var button = Ui.Flat(content, name + ", " + Ago(session.Started), Ui.D(9));
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.Click += (_, _) => { CloseHiddenMenu(true); Activate(session); };
        return new Border
        {
            Height = Ui.D(40), CornerRadius = new CornerRadius(Ui.D(10)), Margin = new Thickness(0, 0, 0, Ui.D(1)),
            Background = selected ? Br.RowSel : Br.Clear,
            BorderBrush = selected ? Br.RowSelEdge : Br.Clear,
            BorderThickness = new Thickness(1),
            Child = button
        };
    }

    private void HideFlyout(bool immediate)
    {
        var popup = _hmFly;
        var box = _hmFlyBox;
        _hmFly = null;
        _hmFlyBox = null;
        _overFly = false;
        if (popup == null) return;
        if (immediate || box == null) popup.IsOpen = false;
        else Anim.PopOut(box, -8, 0, 110, () => popup.IsOpen = false);
    }

    /// <summary>Closes the list and the strip. immediate = no animation (a click, or the bar was shown).</summary>
    private void CloseHiddenMenu(bool immediate)
    {
        _hmOpenTimer?.Stop();
        _hmCloseTimer?.Stop();
        _hmFlyTimer?.Stop();
        _flyWanted = false;
        _overStrip = false;
        HideFlyout(immediate);
        var popup = _hmStrip;
        var box = _hmStripBox;
        _hmStrip = null;
        _hmStripBox = null;
        if (popup == null) return;
        if (immediate || box == null) popup.IsOpen = false;
        else Anim.PopOut(box, 0, -6, 110, () => popup.IsOpen = false);
    }
}
