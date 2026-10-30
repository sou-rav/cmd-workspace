using System.Runtime.InteropServices;
using CmdWorkspace.Core;
using CmdWorkspace.Term;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CmdWorkspace;

/// <summary>
/// The one main window of the Kero design: a left bar and one terminal card.
/// Frame 1458 x 940 screenshot pixels at 125 percent = 1166 x 752 DIPs. Every size below is "Ui.D(mockup pixels)".
/// </summary>
public sealed partial class MainWindow : Window
{
    private const double FrameWidth = 1166, FrameHeight = 752, MinClientW = 900, MinClientH = 580;   // smallest size (user choice 2026-10-25)

    private readonly SessionManager _sessions = new();
    private readonly TerminalHost _term = new();
    private Session? _active;
    private bool _forceClose, _dialogOpen;
    private SettingsView? _settings;      // the Settings panel, shown inside this window over a dimmed layer (option C)
    private readonly Grid _panelHost = new();
    private readonly Grid _dim = new();
    private readonly SolidColorBrush _dimBrush = new();
    private readonly Image _snap = new();   // picture of the terminal, shown while the panel is open
    private bool _settingsOpen;
    private string _snapTheme = "";       // theme that was active when the snapshot of the terminal was taken
    private int _settingsToken;           // stops an older open or close animation from finishing after a newer one started

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(20) };

    private readonly Grid _cardHost = new();
    private readonly Border _card = new();
    private readonly StackPanel _hint = new();
    private readonly TextBlock _title = new();
    private readonly StackPanel _groups = new();
    private readonly ScrollViewer _barScroll = new();
    private readonly StackPanel _topIcons = new();
    private readonly StackPanel _bottomIcons = new();
    private readonly Grid _drag = new();
    private bool _lastBarOpen = true;
    private readonly Grid _barContent = new();
    private readonly Border _hl = new();                       // the selection highlight, one element that glides between rows
    private readonly TranslateTransform _hlMove = new();
    private readonly Dictionary<Session, Grid> _rows = new();
    private readonly Dictionary<Session, int> _markKinds = new();
    private Grid? _selRow;
    private bool _hlShown;
    private double _hlY;
    private Session? _fadeSession;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    // Another start (cws, right click item, Start menu) gave its folder to this window: bring it to the front and open a NEW session there (D3).
    private void OnOutsideRequest(string? folder)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // restore
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex) { Log.Error("bring to front", ex); }
        if (folder == null) return;
        Log.Write("start folder requested (running window): " + folder);
        if (_settingsOpen) CloseSettings();
        _ = OpenFolderAsync(folder);
    }

    // Default terminal (Phase 12b): Windows hands over a console program. Runs on a background thread, the session is made on the UI thread.
    private bool AcceptHandoff(DefaultTerminal.HandoffInfo info)
    {
        var claimed = 0;      // 0 waiting, 1 the window took it, 2 gave up (too slow)
        var done = new ManualResetEventSlim(false);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return;
                TakeHandoff(info);
            }
            catch (Exception ex) { Log.Error("default terminal: take over", ex); }
            finally { done.Set(); }
        })) return false;
        if (done.Wait(8000)) return claimed == 1;
        if (Interlocked.CompareExchange(ref claimed, 2, 0) == 0) return false;
        done.Wait(8000);
        return true;
    }

    private void TakeHandoff(DefaultTerminal.HandoffInfo info)
    {
        // From here the handles belong to the session.
        var pty = Pty.Attach(info.FromConsole, info.ToConsole, info.Client, info.Signal, info.Reference, info.Server);
        info.FromConsole = info.ToConsole = info.Client = info.Signal = info.Reference = info.Server = IntPtr.Zero;
        var session = _sessions.AddAttached(ShellForClient(info.ClientExe), HomeFolder, pty);
        session.StateChanged += _ => DispatcherQueue.TryEnqueue(RenderBar);
        _term.Add(session);
        _fadeSession = session;
        if (_settingsOpen) CloseSettings();
        Activate(session);
        session.Resize(_term.Cols, _term.Rows);
        OnOutsideRequest(null);   // bring the window to the front
    }

    // The shell name in the bar. A known shell keeps its normal name, any other console program shows its file name.
    private static ShellInfo ShellForClient(string exe)
    {
        if (exe.Length > 0)
        {
            var known = Shells.All.FirstOrDefault(s => s.IsInstalled && s.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
            if (known != null) return known;
            var name = Path.GetFileNameWithoutExtension(exe);
            return new ShellInfo("handoff", name, name, exe, "");
        }
        return new ShellInfo("handoff", "Console", "Console", "", "");
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = Elevation.IsElevated ? "CMD Workspace (Administrator)" : "CMD Workspace";

        BuildChrome();
        ApplyAll();
        ApplyWindowIcon();
        LayoutBar();
        RenderBar();

        Caption.RemoveSystemBar(this);
        Caption.MakeDraggable(this, _drag, true);

        // ResizeClient works in physical pixels, so multiply by the display scale.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(FrameWidth * scale), (int)Math.Round(FrameHeight * scale)));

        // Smallest size (the window frame is added to the client size).
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            var extraW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
            var extraH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
            presenter.PreferredMinimumWidth = (int)Math.Round(MinClientW * scale) + extraW;
            presenter.PreferredMinimumHeight = (int)Math.Round(MinClientH * scale) + extraH;
        }

        Root.SizeChanged += (_, _) => SizePanel();
        _ = Shells.RefreshWslAsync(force: true);   // finds installed Linux distributions in the background
        _term.LinkClicked += OnLinkClicked;
        _term.MultilinePaste += OnMultilinePaste;
        _term.Info += message => Toast(message);
        // The web view stays hidden until its page is ready (otherwise a black square shows for about a second, seen in Open as Administrator).
        _term.Ready += () =>
        {
            // A web view that was just shown paints 2 black frames (about 0.07 s) before its page appears (seen frame by frame in the user's video).
            // So it is shown fully transparent first and made opaque a moment later. The card colour shows through meanwhile.
            _term.Opacity = 0;
            RenderBar();
            if (_active != null && !_settingsOpen) { _term.Show(_active); _term.Focus(); }
            var reveal = DispatcherQueue.CreateTimer();
            reveal.Interval = TimeSpan.FromMilliseconds(150);
            reveal.IsRepeating = false;
            reveal.Tick += (_, _) => _term.Opacity = 1;
            reveal.Start();
        };

        AppState.Changed += OnSettingsChanged;
        ProjectStore.Changed += OnStoreChanged;
        ProjectStore.Problem += OnStoreProblem;
        Root.Loaded += (_, _) =>
        {
            SingleInstance.StartServer(folder => DispatcherQueue.TryEnqueue(() => OnOutsideRequest(folder)));
            // Phase 12b: only when the user turned the default terminal on in Settings > Shell.
            DefaultTerminal.Accept = AcceptHandoff;
            if (DefaultTerminal.RefreshPath()) DefaultTerminal.StartServer();
            var wanted = Elevation.RequestedShell();
            if (wanted != null && Elevation.IsElevated)
            {
                _ = NewSessionAsync(Shells.ById(wanted));
                Toast("Running as administrator", "Every session in this window has full control of this PC.");
            }
            else
            {
                // Phase 8c: started from a folder (Explorer address bar, Win+R, command line). Always a NEW session there (D3).
                var startFolder = StartFolder.Requested();
                if (startFolder != null)
                {
                    Log.Write("start folder requested: " + startFolder);
                    _ = OpenFolderAsync(startFolder);
                }
            }
        };
        Root.Loaded += (_, _) => { if (ProjectStore.LoadProblem != null) Toast("Saved projects not loaded", ProjectStore.LoadProblem); };
        AppWindow.Closing += OnAppClosing;
        Closed += (_, _) =>
        {
            AppState.Changed -= OnSettingsChanged;
            ProjectStore.Changed -= OnStoreChanged;
            ProjectStore.Problem -= OnStoreProblem;
            _clock.Stop();
            DefaultTerminal.Accept = null;
            DefaultTerminal.StopServer();
            _sessions.CloseAll();
        };
        _clock.Tick += (_, _) => RenderBar();
        _clock.Start();
    }

    // ---------------------------------------------------------------- building the screen

    private void BuildChrome()
    {
        Root.Background = Br.Window;

        // Terminal card with a soft shadow made of a few faint rounded layers behind it.
        _cardHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _cardHost.VerticalAlignment = VerticalAlignment.Stretch;
        for (var i = 6; i >= 1; i--)
        {
            _cardHost.Children.Add(new Border
            {
                Margin = new Thickness(-i, -i + 1, -i, -i - 1),
                CornerRadius = new CornerRadius(Ui.D(22) + i),
                Background = Br.Shadow,
                IsHitTestVisible = false
            });
        }
        _card.Background = Br.Card;
        _card.BorderBrush = Br.CardEdge;
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = new CornerRadius(Ui.D(22));

        // The terminal starts 44 DIPs below the card top (first line at 72 mockup pixels). The 6 DIP inset keeps the web view's square corners inside the round card.
        _term.Margin = new Thickness(6, 44, 6, 6);
        _term.Visibility = Visibility.Collapsed;

        BuildStartScreen();

        var inner = new Grid();
        inner.Children.Add(_term);
        _snap.Margin = _term.Margin;
        _snap.Stretch = Stretch.Fill;
        _snap.IsHitTestVisible = false;
        _snap.Visibility = Visibility.Collapsed;
        inner.Children.Add(_snap);
        inner.Children.Add(_hint);
        _card.Child = inner;
        _cardHost.Children.Add(_card);
        Root.Children.Add(_cardHost);

        // Drag area for the window (right of the two top icons). The system window buttons sit on its right end.
        _drag.Height = 48;
        _drag.VerticalAlignment = VerticalAlignment.Top;
        _drag.HorizontalAlignment = HorizontalAlignment.Stretch;
        _drag.Margin = new Thickness(76, 0, 0, 0);
        _drag.Background = Br.Clear;
        Root.Children.Add(_drag);
        Root.Children.Add(Caption.Buttons(this, true, Ui.D(21)));

        _title.Foreground = Br.Text;
        _title.HorizontalAlignment = HorizontalAlignment.Left;
        _title.VerticalAlignment = VerticalAlignment.Top;
        _title.LineHeight = Ui.D(22);
        _title.IsHitTestVisible = false;
        Root.Children.Add(_title);

        // Left bar: groups of sessions.
        _hl.Background = Br.RowSel;
        _hl.BorderBrush = Br.RowSelEdge;
        _hl.BorderThickness = new Thickness(1);
        _hl.CornerRadius = new CornerRadius(Ui.D(10));
        _hl.Height = Ui.D(43);
        _hl.VerticalAlignment = VerticalAlignment.Top;
        _hl.HorizontalAlignment = HorizontalAlignment.Stretch;
        _hl.IsHitTestVisible = false;
        _hl.Opacity = 0;
        _hl.RenderTransform = _hlMove;
        _barContent.Children.Add(_hl);
        _barContent.Children.Add(_groups);
        _barScroll.Content = _barContent;
        _barScroll.Width = Ui.D(310);
        _barScroll.HorizontalAlignment = HorizontalAlignment.Left;
        _barScroll.VerticalAlignment = VerticalAlignment.Stretch;
        _barScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _barScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _barScroll.Margin = new Thickness(Ui.D(13), Ui.D(80), 0, Ui.D(56));
        Root.Children.Add(_barScroll);

        // Top left: hide or show the bar, new session.
        _topIcons.Orientation = Orientation.Horizontal;
        _topIcons.HorizontalAlignment = HorizontalAlignment.Left;
        _topIcons.VerticalAlignment = VerticalAlignment.Top;
        _topIcons.Margin = new Thickness(15.6, 14.4, 0, 0);
        var toggle = Ui.Flat(Ui.Icon(Icons.Bar, Ui.D(24), Br.Dim), "Show or hide the left bar", 6, Ui.D(37), 32);
        toggle.Click += (_, _) => { AppState.S.BarOpen = !AppState.S.BarOpen; AppState.Notify(); };
        // No tooltip on the collapse icon (user, 2026-10-29): it would pop up right when the hidden-bar strip opens. The name stays for screen readers (Ui.Flat sets it).
        WireHiddenMenu(toggle);
        var plus = Ui.Flat(Ui.Icon(Icons.Plus, Ui.D(24), Br.Dim), "New session", 6, Ui.D(37), 32);
        plus.Click += (_, _) => ShowPlusMenu();
        ToolTipService.SetToolTip(plus, "New session");
        _topIcons.Children.Add(toggle);
        _topIcons.Children.Add(plus);
        Root.Children.Add(_topIcons);

        // Bottom left: open folder, search, settings.
        _bottomIcons.Orientation = Orientation.Horizontal;
        _bottomIcons.HorizontalAlignment = HorizontalAlignment.Left;
        _bottomIcons.VerticalAlignment = VerticalAlignment.Bottom;
        _bottomIcons.Margin = new Thickness(Ui.D(13), 0, 0, 4);
        _bottomIcons.Children.Add(BottomButton(Icons.Folder, "Open folder", ShowFolderMenu));
        _bottomIcons.Children.Add(BottomButton(Icons.Search, "Search", ShowSearch));
        _bottomIcons.Children.Add(BottomButton(Icons.Gear, "Settings", OpenSettings));
        Root.Children.Add(_bottomIcons);
    }

    private static Button BottomButton(string icon, string name, Action click)
    {
        var b = Ui.Flat(Ui.Icon(icon, Ui.D(24), Br.Dim), name, 6, Ui.D(38), 32);
        b.Click += (_, _) => click();
        ToolTipService.SetToolTip(b, name);
        return b;
    }

    // Start screen (Kero home look): icon tile, title, one line, two buttons. Shown when there are no sessions.
    private void BuildStartScreen()
    {
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Spacing = Ui.D(10);

        var tile = new Border
        {
            Width = Ui.D(64), Height = Ui.D(64), CornerRadius = new CornerRadius(Ui.D(16)),
            Background = Br.Hover, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, Ui.D(8)),
            Child = Ui.Icon(Icons.Terminal, Ui.D(28), Br.Dim)
        };
        _hint.Children.Add(tile);

        var title = Ui.T("Start a session", 24, Br.Text);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.Children.Add(title);

        var line = Ui.T("Open a Command Prompt or PowerShell here, or pick a folder to start in.", 16, Br.Dim);
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.TextAlignment = TextAlignment.Center;
        line.TextWrapping = TextWrapping.Wrap;
        line.MaxWidth = Ui.D(460);
        _hint.Children.Add(line);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.D(12), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Ui.D(14), 0, 0) };
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var newBtn = Ui.Flat(Ui.T("New Session", 16, white), "New Session", Ui.D(10), double.NaN, Ui.D(40), new Thickness(Ui.D(18), 0, Ui.D(18), 0));
        newBtn.Background = Br.Accent;
        newBtn.Click += (_, _) => _ = NewSessionAsync(ShellForFolder());
        var openBtn = Ui.Flat(Ui.T("Open Folder...", 16, Br.Text), "Open Folder", Ui.D(10), double.NaN, Ui.D(40), new Thickness(Ui.D(18), 0, Ui.D(18), 0));
        openBtn.Background = Br.Pop;
        openBtn.Click += (_, _) => _ = PickFolderAsync();
        row.Children.Add(newBtn);
        row.Children.Add(new Border { Child = openBtn, BorderBrush = Br.PopEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Ui.D(10)) });
        _hint.Children.Add(row);
    }

    // Theme, background, text size and terminal settings. Safe to call at any time.
    private void ApplyAll()
    {
        var dark = AppState.S.Theme == "Dark";
        Br.Apply(dark ? Palette.Dark : Palette.Light, AppState.S.Background);
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _dimBrush.Color = dark ? Windows.UI.Color.FromArgb(0x78, 0, 0, 0) : Windows.UI.Color.FromArgb(0x55, 28, 20, 40);
        _title.FontSize = Ui.F(15);
        _term.Configure();
    }

    // Bar open or hidden: the card, the title and the bar move (Kero layout).
    private void LayoutBar()
    {
        var open = AppState.S.BarOpen;
        var cardTo = Ui.D(open ? 335 : 10);
        var titleTo = Ui.D(open ? 361 : 118);
        if (open != _lastBarOpen && Anim.Enabled && _cardHost.IsLoaded) StartCardSlide(cardTo, titleTo);
        else if (open != _lastBarOpen || _cardTimer == null) { StopCardSlide(); SetCardLeft(cardTo, titleTo); }
        // (same bar state while a slide runs: it already goes to this target)
        if (open)
        {
            _barToken++;
            _barHiding = false;
            _barScroll.Visibility = Visibility.Visible;
            _bottomIcons.Visibility = Visibility.Visible;
            if (!_lastBarOpen) { Anim.PopIn(_barScroll, -18, 0, 190); Anim.PopIn(_bottomIcons, -18, 0, 190); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, MoveHighlight); }
        }
        else if (_lastBarOpen && Anim.Enabled && _cardHost.IsLoaded)
        {
            // The bar slides out and fades, then it is collapsed.
            _barHiding = true;
            var token = ++_barToken;
            Anim.PopOut(_barScroll, -18, 0, 170, () => HideBarPart(_barScroll, token));
            Anim.PopOut(_bottomIcons, -18, 0, 170, () => HideBarPart(_bottomIcons, token));
        }
        else if (!_barHiding)
        {
            _barScroll.Visibility = Visibility.Collapsed;
            _bottomIcons.Visibility = Visibility.Collapsed;
        }
        _lastBarOpen = open;
        if (open) CloseHiddenMenu(true);
    }

    // ---- card slide: the card and the title follow the bar. The terminal is resized only once, when the slide ends.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cardTimer;

    private void SetCardLeft(double card, double title)
    {
        _cardHost.Margin = new Thickness(card, Ui.D(10), Ui.D(11), Ui.D(10));
        _title.Margin = new Thickness(title, Ui.D(27), 0, 0);
    }

    private void StopCardSlide()
    {
        if (_cardTimer == null) return;
        _cardTimer.Stop();
        _cardTimer = null;
        _term.Freeze(false);
    }

    private void StartCardSlide(double cardTo, double titleTo)
    {
        var cardFrom = _cardHost.Margin.Left;
        var titleFrom = _title.Margin.Left;
        if (_cardTimer == null) _term.Freeze(true);
        else _cardTimer.Stop();
        var timer = DispatcherQueue.CreateTimer();
        _cardTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        var start = Environment.TickCount64;
        const double ms = 190;
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_cardTimer, timer)) { timer.Stop(); return; }
            var t = Math.Min(1, (Environment.TickCount64 - start) / ms);
            var e = 1 - Math.Pow(1 - t, 3);
            SetCardLeft(cardFrom + (cardTo - cardFrom) * e, titleFrom + (titleTo - titleFrom) * e);
            if (t >= 1) { SetCardLeft(cardTo, titleTo); StopCardSlide(); }
        };
        timer.Start();
    }

    private int _barToken;
    private bool _barHiding;

    private void HideBarPart(UIElement part, int token)
    {
        if (token != _barToken || AppState.S.BarOpen) return;   // the bar was opened again meanwhile
        part.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(part, _bottomIcons)) _barHiding = false;
    }

    // Projects, recent folders or the last project changed (from the Settings window or from here).
    private void OnStoreChanged() => RenderBar();

    private void OnStoreProblem(string message) => Toast("Could not save", message);

    private void OnSettingsChanged()
    {
        // The picture of the terminal behind the Settings panel still has the OLD theme colours. Drop it, so the card shows the new colour at once
        // (the live terminal comes back with the new theme when Settings closes).
        if (_settingsOpen && _snap.Source != null && _snapTheme != AppState.S.Theme) { _snap.Source = null; _snap.Visibility = Visibility.Collapsed; }
        ApplyAll();
        ApplyWindowIcon();
        LayoutBar();
        RenderBar();
        UpdateTitle();
    }

    private string? _iconFile;

    /// <summary>Window and taskbar icon follows the app theme (Light or Dark setting, not Windows). The exe file icon is fixed (dark).</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Assets", AppState.S.Theme == "Dark" ? "icon-dark.ico" : "icon-light.ico");
            if (file == _iconFile || !File.Exists(file)) return;
            AppWindow.SetIcon(file);
            _iconFile = file;
        }
        catch (Exception ex) { Log.Error("window icon", ex); }
    }

    // ---------------------------------------------------------------- the left bar

    private static string HomeFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string GroupName(string folder) => Folders.DisplayName(folder);

    /// <summary>The project a session belongs to: the one chosen with Move to Project, else a saved project with the same folder, else none.</summary>
    private static Project? ProjectOf(Session s) => ProjectStore.ById(s.ProjectId) ?? ProjectStore.ByPath(s.Folder);

    /// <summary>Left bar group: a project (by id), or the plain folder. Group names are never merged by name, only by project or folder.</summary>
    private static (string Key, string Name) GroupOf(Session s)
    {
        var project = ProjectOf(s);
        return project != null
            ? ("p:" + project.Id, project.Name)
            : ("f:" + Folders.Normalize(s.Folder).ToUpperInvariant(), GroupName(s.Folder));
    }

    private static string RowName(Session s) => !string.IsNullOrWhiteSpace(s.CustomName) ? s.CustomName! : AppState.S.NameAfter == "Folder" ? GroupName(s.Folder) : s.Shell.ShortName;

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "now";
        if (span.TotalHours < 1) return (int)span.TotalMinutes + "m";
        if (span.TotalDays < 1) return (int)span.TotalHours + "h";
        return (int)span.TotalDays + "d";
    }

    private int _collapsing;

    private void RenderBar()
    {
        if (_collapsing > 0) return;
        _groups.Children.Clear();
        _rows.Clear();
        _selRow = null;
        foreach (var group in _sessions.Sessions.GroupBy(s => GroupOf(s).Key))
        {
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Ui.D(9),
                Height = Ui.D(30),
                Margin = new Thickness(Ui.D(15), 0, 0, Ui.D(6))
            };
            header.Children.Add(Ui.T(GroupOf(group.First()).Name, 15, Br.Dim, semi: true));
            header.Children.Add(Ui.T(group.Count().ToString(), 15, Br.Faint));

            var section = new StackPanel { Margin = new Thickness(0, 0, 0, Ui.D(14)) };
            section.Children.Add(header);
            foreach (var session in group) section.Children.Add(MakeRow(session));
            _groups.Children.Add(section);
        }

        var any = _sessions.Sessions.Count > 0;
        // While the Settings page is open the terminal web view must stay hidden (it would draw on top of the page).
        _term.Visibility = any && _term.IsReady && !_settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        var hintWas = _hint.Visibility == Visibility.Visible;
        _hint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any && !hintWas && _cardHost.IsLoaded) Anim.PopIn(_hint, 0, 8, 200);   // the empty page fades in after the last session closed
        foreach (var gone in _markKinds.Keys.Where(k => !_sessions.Sessions.Contains(k)).ToList()) _markKinds.Remove(gone);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, MoveHighlight);
    }

    /// <summary>Puts the selection highlight behind the selected row. It glides (180 ms) when the selection or the row position changed.</summary>
    private void MoveHighlight()
    {
        try
        {
            if (_selRow == null)
            {
                if (_hlShown) { Anim.Fade(_hl, 0, 120); _hlShown = false; }
                return;
            }
            if (_barScroll.Visibility != Visibility.Visible || _barHiding) return;
            _barContent.UpdateLayout();
            if (_selRow.ActualHeight <= 0) return;
            var y = _selRow.TransformToVisual(_barContent).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            _hl.Height = _selRow.ActualHeight;
            if (!_hlShown)
            {
                Anim.SetY(_hlMove, y);
                _hlY = y;
                _hlShown = true;
                Anim.Fade(_hl, 1, 150);
            }
            else if (Math.Abs(y - _hlY) > 0.5)
            {
                _hlY = y;
                Anim.SlideY(_hlMove, y, 180);
            }
        }
        catch (Exception ex) { Log.Error("highlight", ex); }
    }

    private UIElement MakeRow(Session session)
    {
        var selected = session == _active;
        var name = RowName(session);

        var row = new Grid
        {
            Height = Ui.D(43),
            Margin = new Thickness(0, 0, 0, Ui.D(1)),
            CornerRadius = new CornerRadius(Ui.D(10)),
            Background = Br.Clear,
            BorderBrush = Br.Clear,
            BorderThickness = new Thickness(1)
        };
        _rows[session] = row;
        if (selected) _selRow = row;

        var content = new Grid { Margin = new Thickness(Ui.D(16), 0, Ui.D(13), 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = Ui.T(name, 16, Br.Text);
        var time = Ui.T(Ago(session.Started), 15, Br.Faint);
        time.Margin = new Thickness(0, 0, Ui.D(13), 0);
        Grid.SetColumn(time, 1);
        var mark = new Grid { Width = Ui.D(18), Height = Ui.D(18) };
        mark.Children.Add(MarkFor(session));
        var kind = session.State == SessionState.Exited ? (session.ExitCode == 0 ? 1 : 2) : 0;
        if (_markKinds.TryGetValue(session, out var lastKind) && lastKind != kind) mark.Loaded += (_, _) => Anim.PopIn(mark, 0, 4, 220);   // dot, check or cross changed: it fades in
        _markKinds[session] = kind;
        Grid.SetColumn(mark, 2);
        content.Children.Add(label);
        content.Children.Add(time);
        content.Children.Add(mark);

        var button = Ui.Flat(content, name + ", " + Ago(session.Started), Ui.D(10));
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.Click += (_, _) => Activate(session);
        row.Children.Add(button);

        // On hover the time and the mark give way to a close button (user choice, 2026-10-24).
        var close = Ui.Flat(Ui.Icon(Icons.Close, Ui.D(16), Br.Dim), "Close " + name, 6, Ui.D(24), Ui.D(24));
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Center;
        close.Margin = new Thickness(0, 0, Ui.D(10), 0);
        close.Opacity = 0;
        close.IsHitTestVisible = false;
        close.Click += async (_, _) => await CloseSessionAsync(session);
        row.Children.Add(close);
        if (session == _fadeSession) { _fadeSession = null; row.Loaded += (_, _) => Anim.PopIn(row, -12, 0, 200); }
        row.RightTapped += (_, e) => { var at = e.GetPosition(Root); e.Handled = true; ShowRowMenu(session, at.X, at.Y); };
        row.PointerEntered += (_, _) => { close.IsHitTestVisible = true; Anim.Fade(close, 1, 110); Anim.Fade(time, 0, 110); Anim.Fade(mark, 0, 110); };
        row.PointerExited += (_, _) => { close.IsHitTestVisible = false; Anim.Fade(close, 0, 110); Anim.Fade(time, 1, 110); Anim.Fade(mark, 1, 110); };
        return row;
    }

    // Green dot = live. Check = ended with code 0. Red cross = ended with an error. Never claims "idle".
    private static FrameworkElement MarkFor(Session s)
    {
        if (s.State is SessionState.Live or SessionState.Starting)
            return new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = Ui.D(8), Height = Ui.D(8), Fill = Br.Green,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        if (s.State == SessionState.Exited && s.ExitCode == 0) return Ui.Icon(Icons.Check, Ui.D(18), Br.Mark);
        return Ui.Icon(Icons.Close, Ui.D(16), Br.Red);
    }

    // The card shows no title (user choice, 2026-10-25).
    private void UpdateTitle() => _title.Text = "";

    // ---------------------------------------------------------------- sessions

    private void Activate(Session session)
    {
        CloseMenu();
        if (_settingsOpen) CloseSettings();
        _active = session;
        var project = ProjectOf(session);
        if (project != null) ProjectStore.SetLastProject(project.Id);   // remembered for the next start
        RenderBar();
        UpdateTitle();
        _term.Show(session);
        _term.Focus();
    }

    // Where a new session from the plus button starts (Settings > General > New sessions start in).
    // If the wanted folder is not usable, the session starts in Home and the notice says why. Nothing is hidden.
    private async Task<(string Folder, (string Title, string Sub)? Note)> ResolveStartFolderAsync()
    {
        string? wanted = null, what = null;
        switch (AppState.S.StartIn)
        {
            case "Current" when _active != null:
                wanted = _active.Folder;
                what = "the current folder";
                break;
            case "Project":
                var project = ProjectStore.ById(ProjectStore.Data.LastProjectId);
                if (project == null) return (HomeFolder, ("Started in Home", "No project has been used yet. Open a folder from a project, or set one in Settings > General."));
                wanted = project.Path;
                what = "the project \"" + project.Name + "\"";
                break;
        }
        if (wanted == null) return (HomeFolder, null);
        var state = await Folders.CheckAsync(wanted);
        if (state == FolderState.Ok) return (wanted, null);
        return (HomeFolder, ("Started in Home", "The folder of " + what + " is not usable (" + Folders.Tag(state).ToLowerInvariant() + "): " + wanted));
    }

    // The plus button, the start screen button and the shell items of the plus menu.
    private async Task NewSessionAsync(ShellInfo shell)
    {
        var (folder, note) = await ResolveStartFolderAsync();
        var session = StartSession(shell, folder, remember: false);
        if (session != null && note != null) Toast(note.Value.Title, note.Value.Sub);
    }

    // A folder the user chose (Open Folder, a project, a recent folder): checked first, then ALWAYS a new session (decision D3).
    private async Task OpenFolderAsync(string folder)
    {
        var state = await Folders.CheckAsync(folder);
        if (state != FolderState.Ok)
        {
            Toast("Folder not opened: " + Folders.Tag(state).ToLowerInvariant(), folder + "\n" + Folders.Sentence(state) + " Your sessions were not changed.");
            return;
        }
        StartSession(ShellForFolder(), folder, remember: true);
    }

    // Choosing a folder or a shell ALWAYS makes a new session (decision D3). The start folder goes to CreateProcess. Nothing is typed into a shell.
    // Only folders the user chose are added to the recent list, not the plain Home start of the plus button.
    private Session? StartSession(ShellInfo shell, string folder, bool remember)
    {
        var session = _sessions.Create(shell, folder, _term.Cols, _term.Rows, out var problem);
        if (session == null)
        {
            Toast("Could not start the session", problem);
            return null;
        }
        session.StateChanged += _ => DispatcherQueue.TryEnqueue(RenderBar);
        _term.Add(session);
        _fadeSession = session;
        if (remember) ProjectStore.RememberRecent(folder);
        Activate(session);
        return session;
    }

    private ShellInfo ShellForFolder() => _active?.Shell ?? Shells.ById(AppState.S.DefaultShell);

    private async Task PickFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;   // cancelled: nothing changes
            if (string.IsNullOrEmpty(folder.Path)) { Toast("Folder not available", "Windows gave no path for this folder."); return; }
            await OpenFolderAsync(folder.Path);
        }
        catch (Exception ex)
        {
            Log.Error("folder picker", ex);
            Toast("The folder dialog could not open", ex.Message);
        }
    }

    private async Task CloseSessionAsync(Session session)
    {
        if (session.State == SessionState.Live && AppState.S.AskBeforeClose)
        {
            var running = session.HasChildren();
            var text = running
                ? "A program is still running in " + RowName(session) + ". Closing the session ends it."
                : "The shell in " + RowName(session) + " is still running. Closing the session ends it.";
            if (!await Confirm("Close this session?", text, "Close session")) return;
        }
        if (_rows.TryGetValue(session, out var row))
        {
            if (session == _active) Anim.Fade(_hl, 0, 120);
            _collapsing++;   // the bar is not rebuilt while the row animates out
            try { await Anim.CollapseAsync(row, -16, 180); }   // the row fades out and the rows below move up
            finally { _collapsing--; }
            if (session == _active) _hlShown = false;
        }
        _term.Remove(session);
        _sessions.Remove(session);
        if (_active == session) _active = _sessions.Sessions.LastOrDefault();
        RenderBar();
        UpdateTitle();
        if (_active != null) _term.Show(_active);
    }

    private async void OnAppClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var live = _sessions.Sessions.Count(s => s.State == SessionState.Live);
        if (_forceClose || live == 0 || !AppState.S.AskBeforeClose) return;
        args.Cancel = true;
        var text = live == 1 ? "1 session is still running. Closing the app ends it." : live + " sessions are still running. Closing the app ends them.";
        if (!await Confirm("Close CMD Workspace?", text, "Close app")) return;
        _forceClose = true;
        _sessions.CloseAll();
        Close();
    }

    private async void OnLinkClicked(Session session, string uri)
    {
        var problem = await LinkOpener.OpenAsync(uri);
        if (problem != null) Toast("Link not opened", problem);
    }

    private async void OnMultilinePaste(Session session, string text)
    {
        var ok = !AppState.S.AskMultiline || await ConfirmPaste(text);
        if (ok && session.State == SessionState.Live) _term.PasteText(session, text);
        else _term.Focus();
    }

    // ---------------------------------------------------------------- settings panel (option C)

    // About 80 percent of the window width and 85 percent of its height, never bigger than 940 x 640 DIPs, always centred.
    private void SizePanel()
    {
        _panelHost.Width = Math.Min(Ui.D(1175), Math.Max(Ui.D(560), Root.ActualWidth * 0.8));
        _panelHost.Height = Math.Min(Ui.D(800), Math.Max(Ui.D(380), Root.ActualHeight * 0.85));
    }

    private void BuildSettingsLayers()
    {
        // Dim layer over the whole window. A click on it closes the panel.
        _dim.Background = _dimBrush;
        _dim.Opacity = 0;
        _dim.Visibility = Visibility.Collapsed;
        _dim.PointerPressed += (_, e) => { e.Handled = true; CloseSettings(); };

        // Panel without a shadow (user choice, 2026-10-26).
        _panelHost.HorizontalAlignment = HorizontalAlignment.Center;
        _panelHost.VerticalAlignment = VerticalAlignment.Center;
        _panelHost.Visibility = Visibility.Collapsed;
        _settings = new SettingsView(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _settings.CloseRequested += CloseSettings;
        _settings.Notice += (title, text) => Toast(title, text);
        _panelHost.Children.Add(_settings);
        Root.Children.Add(_dim);
        Root.Children.Add(_panelHost);
        SizePanel();
    }

    // The gear opens and closes the Settings panel inside this window. Terminal sessions keep running underneath.
    // Opening: a picture of the terminal replaces the live web view (a web view cannot be dimmed), the window dims, the panel fades in and slides up a little (about 0.25 s).
    private async void OpenSettings()
    {
        CloseMenu();
        if (_settingsOpen) { CloseSettings(); return; }
        _settingsOpen = true;
        var token = ++_settingsToken;

        if (_settings == null) BuildSettingsLayers();
        SizePanel();

        if (_term.Visibility == Visibility.Visible)
        {
            var picture = await _term.CaptureAsync();
            if (token != _settingsToken || !_settingsOpen) return;   // closed again while waiting
            if (picture != null) { _snap.Source = picture; _snap.Visibility = Visibility.Visible; _snapTheme = AppState.S.Theme; }
        }
        _term.Visibility = Visibility.Collapsed;       // a web view always draws on top, so it is hidden

        _dim.Visibility = Visibility.Visible;
        _panelHost.Visibility = Visibility.Visible;
        _settings!.Opening();
        _dim.Opacity = 0;
        Anim.Fade(_dim, 1, 200);
        Anim.PopIn(_panelHost, 0, 16, 260);
    }

    private void CloseSettings()
    {
        if (!_settingsOpen || _settings == null) return;
        _settingsOpen = false;
        var token = ++_settingsToken;
        _settings.Closing();

        Anim.Fade(_dim, 0, 160);
        Anim.Fade(_panelHost, 0, 150);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(170);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (token != _settingsToken || _settingsOpen) return;
            _dim.Visibility = Visibility.Collapsed;
            _panelHost.Visibility = Visibility.Collapsed;
            _snap.Visibility = Visibility.Collapsed;
            _snap.Source = null;
            RenderBar();                                // shows the terminal web view again
            if (_active != null) { _term.Show(_active); _term.Focus(); }
        };
        timer.Start();
    }
}
