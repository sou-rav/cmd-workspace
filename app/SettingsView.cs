using System.Diagnostics;
using CmdWorkspace.Core;
using CmdWorkspace.Term;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using Windows.Graphics;

namespace CmdWorkspace;

/// <summary>
/// The Settings panel (Kero style), shown INSIDE the main window as a centred panel over the dimmed window
/// (user decision 2026-10-25, option C): a category list on the left, one pane on the right. It is not a separate window.
/// Built in code from design/kero-mockup/Window.dc.html. Only rows that really work are shown.
/// Not built on purpose: Environment variables, Keyboard shortcut editing, Font choice, Weight, Ligatures.
/// </summary>
public sealed class SettingsView : Grid
{
    private static readonly (string Id, string Name, string Icon)[] Categories =
    {
        ("general", "General", Icons.Gear),
        ("appearance", "Appearance", Icons.Brush),
        ("terminal", "Terminal", Icons.Terminal),
        ("shell", "Shell", Icons.Code),
        ("keyboard", "Keyboard", Icons.Keyboard),
        ("advanced", "Advanced", Icons.Sliders),
    };

    private bool _terminalBuilt;
    private string _lastLook = "";
    private int _pageSize;
    private readonly Grid _paneGrid = new();
    private bool _pvDrawn;
    private WebView2? _pv;
    private Border? _pvCard;
    private bool _pvReady;
    private string? _pvJson;

    // The last page is kept while the app runs only. After the app is closed and started again, Settings opens on General.
    private static string _lastCategory = "general";
    private string _category = _lastCategory;
    private string _filter = "";
    private bool _dialogOpen;

    private Grid _root => this;
    private readonly StackPanel _rail = new();
    private readonly TextBlock _paneTitle = new();
    private readonly ScrollViewer _scroll = new();
    private FloatingScrollbar _bar = null!;
    private TextBox? _searchBox;

    private readonly IntPtr _ownerHwnd;
    private bool _stale;
    private Button _closeButton = null!;

    /// <summary>Raised when the user presses Escape. The main window then closes the page.</summary>
    public event Action? CloseRequested;

    public SettingsView(IntPtr ownerHwnd)
    {
        _ownerHwnd = ownerHwnd;
        Build();
        PrewarmPreview();
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; CloseRequested?.Invoke(); }
        };
        AppState.Changed += Refresh;
        ProjectStore.Changed += OnStoreChanged;
        Refresh();
    }

    /// <summary>Called just before the page is shown again. Catches up with anything that changed while it was hidden.</summary>
    public void Opening()
    {
        if (_stale) { _stale = false; _lastLook = ""; Refresh(); }   // Refresh redraws the rail and the page
        PositionPreview();
        _closeButton.Focus(FocusState.Programmatic);   // so that Escape reaches this panel
        _ = RefreshShellsAsync();
    }

    // A Linux distribution installed since the app started shows on the Shell page.
    private async Task RefreshShellsAsync()
    {
        var before = Shells.All.Count();
        await Shells.RefreshWslAsync();
        if (Shells.All.Count() != before && _category == "shell") RenderPane();
    }

    /// <summary>Remembers where the user was scrolled. Called when the page is closed.</summary>
    public void Closing() => ScrollMemory[_category] = _scroll.VerticalOffset;

    // Projects or recent folders changed: redraw the General page where the user is looking, same scroll spot.
    private void OnStoreChanged()
    {
        if (_category != "general") return;
        if (Visibility != Visibility.Visible) { _stale = true; return; }
        ScrollMemory[_category] = _scroll.VerticalOffset;
        RenderPane();
    }

    // ---------------------------------------------------------------- frame

    private void Build()
    {
        _root.Background = Br.SetWindow;
        _root.CornerRadius = new CornerRadius(Ui.D(18));

        // Left: search and category list
        var left = new Grid { Width = Ui.D(224), HorizontalAlignment = HorizontalAlignment.Left };

        var searchRow = new Grid { ColumnSpacing = Ui.D(10), Padding = new Thickness(Ui.D(12), 0, Ui.D(12), 0) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.Children.Add(Ui.Icon(Icons.Search, Ui.D(17), Br.Dim));
        _searchBox = FlatTextBox("Search", 15);
        _searchBox.TextChanged += (_, _) => { _filter = _searchBox.Text.Trim(); RenderRail(); };
        Grid.SetColumn(_searchBox, 1);
        searchRow.Children.Add(_searchBox);
        var searchBorder = new Border
        {
            Height = Ui.D(32), Width = Ui.D(200), CornerRadius = new CornerRadius(Ui.D(8)), Background = Br.SearchBox,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Ui.D(13), Ui.D(22), 0, 0), Child = searchRow
        };
        left.Children.Add(searchBorder);

        _rail.Width = Ui.D(200);
        _rail.HorizontalAlignment = HorizontalAlignment.Left;
        _rail.VerticalAlignment = VerticalAlignment.Top;
        _rail.Margin = new Thickness(Ui.D(13), Ui.D(22 + 32 + 15), 0, 0);
        left.Children.Add(_rail);

        var folderLink = Ui.Flat(
            new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = Ui.D(8), HorizontalAlignment = HorizontalAlignment.Center,
                Children = { Ui.Icon(Icons.Code, Ui.D(17), Br.Dim), Ui.T("Open config file", 15, Br.Dim) }
            },
            "Open config file", 8, Ui.D(200), Ui.D(36));
        folderLink.HorizontalAlignment = HorizontalAlignment.Left;
        folderLink.VerticalAlignment = VerticalAlignment.Bottom;
        folderLink.Margin = new Thickness(Ui.D(13), 0, 0, Ui.D(14));
        folderLink.Click += (_, _) => OpenFile(AppState.FilePath);
        left.Children.Add(folderLink);
        _root.Children.Add(left);

        // Right: the pane
        var pane = new Border
        {
            Margin = new Thickness(Ui.D(224), Ui.D(8), Ui.D(8), Ui.D(8)),
            Background = Br.SetPane,
            CornerRadius = new CornerRadius(Ui.D(18))
        };
        var paneGrid = _paneGrid;
        _paneTitle.Foreground = Br.Text;
        _paneTitle.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        _paneTitle.HorizontalAlignment = HorizontalAlignment.Left;
        _paneTitle.VerticalAlignment = VerticalAlignment.Top;
        _paneTitle.Margin = new Thickness(Ui.D(30), Ui.D(17), 0, 0);
        _paneTitle.IsHitTestVisible = false;
        _scroll.Margin = new Thickness(Ui.D(30), Ui.D(75), Ui.D(30), 0);
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        // Kero style floating scroll bar: fades out when idle, with a gap to the pane edges (replaces the Windows bar).
        _bar = new FloatingScrollbar(_scroll, Ui.D(75) + Ui.D(8), Ui.D(14), Ui.D(7));
        // A web view draws above other content, so it is hidden once the page is scrolled (the card keeps its height).
        _scroll.ViewChanged += (_, _) => PositionPreview();
        paneGrid.Children.Add(_paneTitle);
        paneGrid.Children.Add(_scroll);
        paneGrid.Children.Add(_bar.Host);
        var closeButton = _closeButton = Ui.Flat(Ui.Icon(Icons.Close, Ui.D(18), Br.Dim), "Close settings", Ui.D(8), Ui.D(34), Ui.D(34));
        closeButton.HorizontalAlignment = HorizontalAlignment.Right;
        closeButton.VerticalAlignment = VerticalAlignment.Top;
        closeButton.Margin = new Thickness(0, Ui.D(12), Ui.D(14), 0);
        closeButton.Click += (_, _) => CloseRequested?.Invoke();
        ToolTipService.SetToolTip(closeButton, "Close settings (Esc)");
        paneGrid.Children.Add(closeButton);
        pane.Child = paneGrid;
        _root.Children.Add(pane);

    }

    private static TextBox FlatTextBox(string placeholder, double px)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = Br.Text,
            FontSize = Ui.F(px),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            MinHeight = 0
        };
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                                    "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        return box;
    }

    // Called at start and whenever any setting changes (also from the main window).
    private void Refresh()
    {
        if (Visibility != Visibility.Visible && _railItems.Count > 0) { _stale = true; return; }
        var dark = AppState.S.Theme == "Dark";
        Br.Apply(dark ? Palette.Dark : Palette.Light, AppState.S.Background);
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _paneTitle.FontSize = Ui.F(20);

        // The controls keep their own state, so most changes need no rebuild (no flicker). Rebuild only when the look changed,
        // when the Terminal page was changed from outside (text size with Ctrl+= or Ctrl+-), or on Advanced (Reset).
        var look = S.Theme + "|" + S.Background + "|" + S.TextScale;
        var rebuild = look != _lastLook || _category == "advanced"
            || (_category == "terminal" && (!_terminalBuilt || (int)Math.Round(S.FontSize) != _pageSize));
        if (!rebuild)
        {
            if (_category == "terminal") UpdatePreview();
            return;
        }
        _lastLook = look;
        RenderRail();
    }

    // ---------------------------------------------------------------- left list (built once, only the colours change, with a short fade)

    private sealed class RailItem
    {
        public string Id = "";
        public SolidColorBrush Bg = new(), Edge = new(), Icon = new();
    }

    private readonly List<RailItem> _railItems = new();

    private static Windows.UI.Color RailEdge(bool dark) => dark ? Colors.Transparent : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
    private static Windows.UI.Color RailHover(bool dark) => dark ? Windows.UI.Color.FromArgb(0x14, 255, 255, 255) : Windows.UI.Color.FromArgb(0x0A, 0, 0, 0);

    private void Fade(SolidColorBrush brush, Windows.UI.Color to)
    {
        Anim.ColorTo(brush, to, 130);
    }

    private void Select(string id)
    {
        if (id == _category) return;
        ScrollMemory[_category] = _scroll.VerticalOffset;
        _category = id;
        _lastCategory = id;
        var dark = S.Theme == "Dark";
        var p = dark ? Palette.Dark : Palette.Light;
        foreach (var it in _railItems)
        {
            var on = it.Id == id;
            Fade(it.Bg, on ? p.RailSel : Colors.Transparent);
            Fade(it.Edge, on ? RailEdge(dark) : Colors.Transparent);
            Fade(it.Icon, on ? p.Accent : p.Dim);
        }
        RenderPane(true);
    }

    private void RenderRail()
    {
        _rail.Children.Clear();
        _railItems.Clear();
        var dark = S.Theme == "Dark";
        var p = dark ? Palette.Dark : Palette.Light;
        var shown = Categories.Where(c => _filter.Length == 0 || c.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (shown.Count > 0 && !shown.Any(c => c.Id == _category) && _filter.Length > 0) _category = shown[0].Id;
        foreach (var (id, name, icon) in shown)
        {
            var selected = id == _category;
            var item = new RailItem
            {
                Id = id,
                Bg = new SolidColorBrush(selected ? p.RailSel : Colors.Transparent),
                Edge = new SolidColorBrush(selected ? RailEdge(dark) : Colors.Transparent),
                Icon = new SolidColorBrush(selected ? p.Accent : p.Dim)
            };
            var row = new Grid { ColumnSpacing = Ui.D(14), Margin = new Thickness(Ui.D(15), 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Ui.Icon(icon, Ui.D(20), item.Icon));
            var label = Ui.T(name, 16, Br.Text);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var pill = new Border
            {
                Height = Ui.D(40), CornerRadius = new CornerRadius(Ui.D(10)), Background = item.Bg, BorderBrush = item.Edge,
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, Ui.D(2.5)), Child = row
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pill, name);
            var captured = item;
            pill.PointerEntered += (_, _) => { if (captured.Id != _category) Fade(captured.Bg, RailHover(dark)); };
            pill.PointerExited += (_, _) => { if (captured.Id != _category) Fade(captured.Bg, Colors.Transparent); };
            pill.Tapped += (_, _) => Select(captured.Id);
            _railItems.Add(item);
            _rail.Children.Add(pill);
        }
        if (shown.Count == 0) _rail.Children.Add(Ui.T("No match", 15, Br.Dim));
        RenderPane();
    }

    private static readonly Dictionary<string, double> ScrollMemory = new();

    private void RenderPane(bool animate = false)
    {
        var stack = new StackPanel();
        _terminalBuilt = false;
        _pvCard = null;
        _paneTitle.Text = Categories.First(c => c.Id == _category).Name;
        switch (_category)
        {
            case "general": General(stack); break;
            case "appearance": Appearance(stack); break;
            case "terminal": TerminalPage(stack); break;
            case "shell": ShellPage(stack); break;
            case "keyboard": KeyboardPage(stack); break;
            default: Advanced(stack); break;
        }
        _bar.Quiet();
        stack.SizeChanged += (_, _) => _bar.Update();   // the page got longer or shorter
        _scroll.Content = stack;
        _scroll.UpdateLayout();
        _scroll.ChangeView(null, ScrollMemory.TryGetValue(_category, out var offset) ? offset : 0, null, true);   // back to where you left off
        PositionPreview();
        if (animate) Anim.PopIn(stack, 0, 8, 180);
    }

    // ---------------------------------------------------------------- pages

    private static AppSettings S => AppState.S;

    private void General(StackPanel page)
    {
        var startIn = S.StartIn == "Current" ? 1 : S.StartIn == "Project" ? 2 : 0;
        Section(page, "Sessions",
            Row("New sessions start in", "The plus button starts here. Project means the project you used last. Open Folder always starts in the folder you pick.",
                Seg(new[] { "Home", "Current Folder", "Project" }, startIn, i => { S.StartIn = i == 1 ? "Current" : i == 2 ? "Project" : "Home"; AppState.Notify(); })),
            Row("Name sessions after", "The name shown in the left bar.",
                Seg(new[] { "Shell", "Folder" }, S.NameAfter == "Folder" ? 1 : 0, i => { S.NameAfter = i == 1 ? "Folder" : "Shell"; AppState.Notify(); })),
            Row("Ask before closing a live session", "We cannot tell if a shell is idle, so a live session always asks. Turn this off to close without asking.",
                Toggle(S.AskBeforeClose, on => { S.AskBeforeClose = on; AppState.Notify(); })));

        // Projects: one saved folder with a name. Removing a project never touches the folder on disk.
        var rows = new List<UIElement>();
        var tags = new List<(string Path, TextBlock Tag)>();
        foreach (var project in ProjectStore.Data.Projects.ToList())
        {
            var p = project;
            var tag = NoWrap(Ui.T("", 14, Br.Red));
            tags.Add((p.Path, tag));
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.D(10) };
            controls.Children.Add(tag);
            controls.Children.Add(ChipButton("Rename", () => _ = RenameProjectAsync(p)));
            controls.Children.Add(ChipButton("Remove", () => ProjectStore.Remove(p.Id)));
            rows.Add(Row(p.Name, p.Path, controls));
        }
        if (rows.Count == 0)
            rows.Add(Row("No projects yet", "A project is one saved folder with a name. Sessions in that folder are grouped under the name in the left bar.", new Border()));
        rows.Add(Row("Add a project", "Pick a folder. The name starts as the folder name and can be changed. Removing a project only removes it from this list.",
            ChipButton("Add Project...", () => _ = AddProjectAsync())));
        Section(page, "Projects", rows.ToArray());

        // Recent folders: folders you chose, kept between runs. Only the folder and the time, never commands.
        var recentRows = new List<UIElement>();
        foreach (var recent in ProjectStore.Data.Recent.ToList())
        {
            var path = recent.Path;
            var tag = NoWrap(Ui.T("", 14, Br.Red));
            tags.Add((path, tag));
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.D(10) };
            controls.Children.Add(tag);
            if (ProjectStore.ByPath(path) == null)
                controls.Children.Add(ChipButton("Save as Project", () =>
                {
                    var created = ProjectStore.Add(path, null, out var problem);
                    if (created == null) _ = InfoAsync("Not saved", problem ?? "");
                }));
            controls.Children.Add(ChipButton("Remove", () => ProjectStore.RemoveRecent(path)));
            recentRows.Add(Row(path, "Opened " + recent.When.ToString("g"), controls));
        }
        if (recentRows.Count == 0)
            recentRows.Add(Row("No recent folders", "Folders you choose with Open Folder, a project or the search appear here. They are kept between runs.", new Border()));
        else
            recentRows.Add(Row("Clear recent folders", "Removes the whole list. Projects are not touched.", ChipButton("Clear", () => ProjectStore.ClearRecent())));
        Section(page, "Recent folders", recentRows.ToArray());

        _ = FillFolderTagsAsync(tags);
    }

    // Marks missing or unusable folders. The check runs in the background, so the page opens at once.
    private static async Task FillFolderTagsAsync(List<(string Path, TextBlock Tag)> tags)
    {
        var states = await Folders.CheckManyAsync(tags.Select(t => t.Path), 1500);
        foreach (var (path, tag) in tags)
            if (states.TryGetValue(path, out var st)) tag.Text = Folders.Tag(st);
    }

    private async Task AddProjectAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;   // cancelled: nothing changes
            if (string.IsNullOrEmpty(folder.Path)) { await InfoAsync("Folder not available", "Windows gave no path for this folder."); return; }
            var created = ProjectStore.Add(folder.Path, null, out var problem);
            if (created == null) await InfoAsync("Project not added", problem ?? "");
        }
        catch (Exception ex)
        {
            Log.Error("add project", ex);
            await InfoAsync("The folder dialog could not open", ex.Message);
        }
    }

    private async Task RenameProjectAsync(Project project)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var box = new TextBox { Text = project.Name, PlaceholderText = "Project name", MaxLength = ProjectStore.MaxNameLength };
            box.SelectAll();
            var dialog = new ContentDialog
            {
                Title = "Rename project", Content = box,
                PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _root.XamlRoot, RequestedTheme = _root.RequestedTheme
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) ProjectStore.Rename(project.Id, box.Text);
        }
        finally { _dialogOpen = false; }
    }

    /// <summary>A short notice for the user. The main window shows it as a toast (it draws above the dimmed window).</summary>
    public event Action<string, string?>? Notice;

    private Task InfoAsync(string title, string text)
    {
        Notice?.Invoke(title, text);
        return Task.CompletedTask;
    }

    private void Appearance(StackPanel page)
    {
        Section(page, "",
            Row("Appearance", "Takes effect at once. It never follows Windows by itself. Running sessions are not affected.",
                Seg(new[] { "Light", "Dark" }, S.Theme == "Dark" ? 1 : 0, i => { S.Theme = i == 1 ? "Dark" : "Light"; AppState.Notify(); })));

        Section(page, "Motion",
            Row("Animations", "Menus, panels and rows move smoothly instead of jumping. Also off when Windows has Animation effects switched off.",
                Toggle(S.Animations, on => { S.Animations = on; AppState.Notify(); })));

        var swatches = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal, ItemWidth = Ui.D(145), ItemHeight = Ui.D(86 + 8 + 24 + 16)
        };
        for (var i = 0; i < Palette.PresetIds.Length; i++)
            swatches.Children.Add(Swatch(Palette.PresetIds[i], Palette.PresetNames[i]));
        Header(page, "Background");
        page.Children.Add(swatches);
        page.Children.Add(new Border { Height = Ui.D(30) });
    }

    private UIElement Swatch(string id, string label)
    {
        var dark = S.Theme == "Dark";
        var selected = S.Background == id;
        var colours = Palette.Preset(id, dark);
        var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = Palette.H(colours[0]) });
        gradient.GradientStops.Add(new GradientStop { Offset = 0.5, Color = Palette.H(colours[1]) });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = Palette.H(colours[2]) });

        // Same look as Kero: a soft tile, a small bar at the left, a white card, and a blue check badge when chosen.
        var face = new Grid { Width = Ui.D(130), Height = Ui.D(86) };
        face.Children.Add(new Border { CornerRadius = new CornerRadius(Ui.D(16)), Background = gradient });
        face.Children.Add(new Border
        {
            Width = Ui.D(14), Height = Ui.D(4), CornerRadius = new CornerRadius(Ui.D(2)), Background = Br.ToggleOff,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Ui.D(11), Ui.D(25), 0, 0)
        });
        face.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Ui.D(10), Ui.D(10), Ui.D(2), Ui.D(2)), Background = Br.Card, BorderBrush = Br.CardEdge, BorderThickness = new Thickness(1),
            Margin = new Thickness(Ui.D(33), Ui.D(15), Ui.D(10), 0)
        });
        if (selected)
        {
            var badge = new Grid
            {
                Width = Ui.D(24), Height = Ui.D(24), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, Ui.D(6), Ui.D(7), 0)
            };
            badge.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = Br.ToggleOn });
            badge.Children.Add(Ui.Icon(Icons.Check, Ui.D(14), Br.White, 3));
            face.Children.Add(badge);
        }

        var stack = new StackPanel { Width = Ui.D(130), Spacing = Ui.D(8) };
        stack.Children.Add(face);
        var text = Ui.T(label, 15, selected ? Br.Text : Br.Dim, bold: selected);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(text);
        var button = Ui.Flat(stack, label + " background", Ui.D(16));
        button.Click += (_, _) => { S.Background = id; AppState.Notify(); };
        return button;
    }

    private void TerminalPage(StackPanel page)
    {
        _terminalBuilt = true;
        page.Children.Add(BuildPreviewCard());
        UpdatePreview();   // the web view is already there, so it only needs the current settings

        var sizes = new[] { 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28 };
        var current = (int)Math.Round(S.FontSize);
        var sizeIndex = 0;
        for (var i = 0; i < sizes.Length; i++) if (Math.Abs(sizes[i] - current) < Math.Abs(sizes[sizeIndex] - current)) sizeIndex = i;
        _pageSize = current;

        var fontLabels = Fonts.All.Select(f => f.Label + (Fonts.IsAvailable(f.Label) ? "" : "   (not installed)")).ToArray();
        var fontIndex = Math.Max(0, Array.FindIndex(Fonts.All, f => f.Label == S.FontFamily));
        var weights = new[] { "Light", "Regular", "Medium", "Semibold" };
        var weightIndex = Math.Max(0, Array.IndexOf(weights, S.Weight));
        var rightModes = new[] { "Pastes", "Menu", "Nothing" };
        var rightLabels = new[] { "Pastes", "Shows Paste menu", "Does nothing" };
        var rightIndex = Math.Max(0, Array.IndexOf(rightModes, S.RightClick));

        Section(page, "Text",
            Row("Font", "JetBrains Mono comes with the app. Fonts installed in Windows work too. A font that is not installed falls back to JetBrains Mono.",
                Dropdown(fontLabels, fontIndex, i => { S.FontFamily = Fonts.All[i].Label; AppState.Notify(); }, pill: true, search: true)),
            Row("Size", "Ctrl+= and Ctrl+- change it too.",
                Dropdown(sizes.Select(x => x + " pt").ToArray(), sizeIndex, i => { _pageSize = sizes[i]; S.FontSize = sizes[i]; AppState.Notify(); })),
            Row("Line height", null,
                LineSlider(S.LineHeight, v => { S.LineHeight = v; AppState.Notify(); })),
            Row("Weight", null,
                Dropdown(weights, weightIndex, i => { S.Weight = weights[i]; AppState.Notify(); })),
            Row("Ligatures", "Join characters as the font draws them, as => and !=.",
                Toggle(S.Ligatures, on => { S.Ligatures = on; AppState.Notify(); })),
            Row("Thicken text", "A heavier stroke for easier reading.",
                Toggle(S.Thicken, on => { S.Thicken = on; AppState.Notify(); })));
        Section(page, "Cursor",
            Row("Shape", "Programs may change it.",
                Seg(new[] { "Block", "Bar", "Underline" }, S.CursorStyle == "bar" ? 1 : S.CursorStyle == "underline" ? 2 : 0,
                    i => { S.CursorStyle = i == 1 ? "bar" : i == 2 ? "underline" : "block"; AppState.Notify(); })),
            Row("Blink", null, Toggle(S.CursorBlink, on => { S.CursorBlink = on; AppState.Notify(); })));
        Section(page, "Input",
            Row("Copy on select", "What you select goes to the clipboard.",
                Toggle(S.CopyOnSelect, on => { S.CopyOnSelect = on; AppState.Notify(); })),
            Row("Right click", null,
                Dropdown(rightLabels, rightIndex, i => { S.RightClick = rightModes[i]; AppState.Notify(); })),
            Row("Show the small Copy menu", "Appears above the text you select.",
                Toggle(S.ShowCopyMenu, on => { S.ShowCopyMenu = on; AppState.Notify(); })),
            Row("Scroll past the last line", "Scroll below the last line into empty space, like Command Prompt. New output or typing brings you back.",
                Toggle(S.ScrollPastEnd, on => { S.ScrollPastEnd = on; AppState.Notify(); })),
            Row("Show the small Paste menu", "Appears above the cursor line when you click it and the clipboard has text.",
                Toggle(S.ShowPasteMenu, on => { S.ShowPasteMenu = on; AppState.Notify(); })),
            Row("Ask before pasting several lines", "Pasted text never runs by itself. No Enter is added.",
                Toggle(S.AskMultiline, on => { S.AskMultiline = on; AppState.Notify(); })));
    }

    // ---------------------------------------------------------------- live preview (a small web page with the same fonts as the terminal)

    private double _pvHeight = Ui.D(110);

    // The preview web view is created when this window opens and then stays in place for good (it is never moved in the tree, which is
    // what crashed before). The Terminal page only holds an empty card; the web view is laid over that card.
    private void PrewarmPreview()
    {
        var pv = new WebView2
        {
            Width = 2, Height = 2, Opacity = 0, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        _pv = pv;
        _paneGrid.Children.Add(pv);
        _ = StartPreviewAsync(pv);
    }

    private Border BuildPreviewCard()
    {
        _pvCard = new Border
        {
            CornerRadius = new CornerRadius(Ui.D(20)), Background = Br.Card, BorderBrush = Br.CardEdge, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, Ui.D(30)), Height = _pvHeight + Ui.D(32) + 2
        };
        _pvCard.Loaded += (_, _) => PositionPreview();
        _pvCard.SizeChanged += (_, _) => PositionPreview();
        return _pvCard;
    }

    // Puts the web view exactly over the card, or hides it when the page is scrolled or another page is shown.
    private void PositionPreview()
    {
        var pv = _pv;
        if (pv == null) return;
        if (_category != "terminal" || _pvCard == null || _pvCard.XamlRoot == null || _scroll.VerticalOffset > 6)
        {
            pv.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var origin = _pvCard.TransformToVisual(_paneGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
            pv.Margin = new Thickness(origin.X + Ui.D(18) + 1, origin.Y + Ui.D(16) + 1, 0, 0);
            pv.Width = Math.Max(10, _pvCard.ActualWidth - Ui.D(36) - 2);
            pv.Height = Math.Max(10, _pvCard.ActualHeight - Ui.D(32) - 2);
            pv.Visibility = Visibility.Visible;
            if (_pvDrawn) pv.Opacity = 1;
        }
        catch (Exception ex) { Log.Error("preview position", ex); pv.Visibility = Visibility.Collapsed; }
    }

    private async Task StartPreviewAsync(WebView2 pv)
    {
        try
        {
            var webDir = Path.Combine(AppContext.BaseDirectory, "Web");
            if (!File.Exists(Path.Combine(webDir, "preview.html"))) { Log.Write("preview.html not found"); return; }
            await pv.EnsureCoreWebView2Async();
            if (pv != _pv) return;
            var core = pv.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("terminal.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("https://terminal.local/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    var kind = doc.RootElement.GetProperty("t").GetString();
                    if (kind == "ready") { _pvReady = true; UpdatePreview(); }
                    else if (kind == "h")
                    {
                        var h = doc.RootElement.GetProperty("h").GetDouble();
                        if (h > 10) _pvHeight = h + 2;
                        if (_pvCard != null) _pvCard.Height = _pvHeight + Ui.D(32) + 2;
                        _pvDrawn = true;
                        PositionPreview();
                    }
                }
                catch (Exception ex) { Log.Error("preview message", ex); }
            };
            core.Navigate("https://terminal.local/preview.html");
        }
        catch (Exception ex) { Log.Error("preview start", ex); }
    }

    private void UpdatePreview()
    {
        if (_pv == null) return;
        var p = S.Theme == "Dark" ? Palette.Dark : Palette.Light;
        var (normal, bold) = Fonts.WeightNumbers(S.Weight);
        _pvJson = JsonSerializer.Serialize(new
        {
            t = "pv",
            fg = Palette.Css(p.Text),
            bg = Palette.Css(p.Card),
            ansi = p.Ansi,
            fontFamily = Fonts.CssList(S.FontFamily),
            fontName = Fonts.Get(S.FontFamily).Css[0],
            size = Math.Round(S.FontSize * S.TextScale),
            lineHeight = S.LineHeight,
            weight = normal,
            bold,
            ligatures = S.Ligatures,
            thicken = S.Thicken
        });
        if (_pvReady && _pv.CoreWebView2 != null) _pv.CoreWebView2.PostWebMessageAsJson(_pvJson);
    }

    // ---------------------------------------------------------------- drop-down list (Kero look: pill button, list with check mark)

    private UIElement Dropdown(string[] items, int selected, Action<int> pick, bool pill = false, bool search = false)
    {
        var sel = Math.Clamp(selected, 0, items.Length - 1);
        var text = NoWrap(Ui.T(items[sel], 16, Br.Text));
        var circle = new Border
        {
            Width = Ui.D(26), Height = Ui.D(26), CornerRadius = new CornerRadius(Ui.D(13)), Background = Br.Divider,
            Child = Ui.Icon(Icons.Chevrons, Ui.D(16), Br.Dim, 1.8)
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.D(10) };
        content.Children.Add(text);
        content.Children.Add(circle);
        UIElement face = pill
            ? new Border { Background = Br.Seg, CornerRadius = new CornerRadius(Ui.D(18)), Padding = new Thickness(Ui.D(14), Ui.D(4), Ui.D(5), Ui.D(4)), Child = content }
            : new Border { Padding = new Thickness(0, 0, Ui.D(4), 0), Child = content };
        var button = Ui.Flat(face, items[sel], Ui.D(18), double.NaN, Ui.D(36));
        button.Click += (_, _) => OpenDropdown(button, items, sel, i => { sel = i; text.Text = items[i]; pick(i); }, search);
        return button;
    }

    private void OpenDropdown(FrameworkElement anchor, string[] items, int selected, Action<int> choose, bool search)
    {
        if (_root.XamlRoot == null) return;
        var rowH = Ui.D(30);
        var width = search ? Ui.D(323) : (items.Max(x => x.Length) > 9 ? Ui.D(150) : Ui.D(93));
        var popup = new Popup { XamlRoot = _root.XamlRoot, IsLightDismissEnabled = true };
        var list = new StackPanel();
        var rows = new List<(Border Row, TextBlock Label, UIElement CheckDark, UIElement CheckLight, int Index)>();
        var highlighted = selected;
        var filter = "";

        void Paint()
        {
            foreach (var r in rows)
            {
                var on = r.Index == highlighted;
                r.Row.Background = on ? Br.Accent : Br.Clear;
                r.Label.Foreground = on ? Br.White : Br.Text;
                r.CheckDark.Visibility = r.Index == selected && !on ? Visibility.Visible : Visibility.Collapsed;
                r.CheckLight.Visibility = r.Index == selected && on ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        void Pick(int index) { popup.IsOpen = false; choose(index); }

        void Build()
        {
            list.Children.Clear();
            rows.Clear();
            var visible = Enumerable.Range(0, items.Length)
                .Where(i => filter.Length == 0 || items[i].Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (visible.Count > 0 && !visible.Contains(highlighted)) highlighted = visible[0];
            foreach (var i in visible)
            {
                var index = i;
                var grid = new Grid { Height = rowH };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.D(30)) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var checkDark = Ui.Icon(Icons.Check, Ui.D(15), Br.Text, 2.2);
                var checkLight = Ui.Icon(Icons.Check, Ui.D(15), Br.White, 2.2);
                var label = Ui.T(items[i], 16, Br.Text);
                Grid.SetColumn(label, 1);
                grid.Children.Add(checkDark);
                grid.Children.Add(checkLight);
                grid.Children.Add(label);
                var row = new Border { CornerRadius = new CornerRadius(Ui.D(7)), Background = Br.Clear, Child = grid };
                row.PointerEntered += (_, _) => { highlighted = index; Paint(); };
                row.Tapped += (_, _) => Pick(index);
                rows.Add((row, label, checkDark, checkLight, index));
                list.Children.Add(row);
            }
            Paint();
        }

        var panel = new StackPanel { Spacing = Ui.D(6) };
        TextBox? box = null;
        if (search)
        {
            box = FlatTextBox("Search", 16);
            var line = new Grid { ColumnSpacing = Ui.D(10), Padding = new Thickness(Ui.D(10), 0, Ui.D(10), 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(Ui.Icon(Icons.Search, Ui.D(16), Br.Dim));
            Grid.SetColumn(box, 1);
            line.Children.Add(box);
            panel.Children.Add(new Border
            {
                Height = Ui.D(36), CornerRadius = new CornerRadius(Ui.D(10)), BorderBrush = Br.Accent, BorderThickness = new Thickness(2), Child = line
            });
            box.TextChanged += (_, _) => { filter = box.Text.Trim(); Build(); };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) { Pick(highlighted); e.Handled = true; }
                else if (e.Key == Windows.System.VirtualKey.Escape) { popup.IsOpen = false; e.Handled = true; }
            };
        }
        Build();
        panel.Children.Add(new ScrollViewer
        {
            Content = list, MaxHeight = Ui.D(330), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var frame = Ui.PopBox(panel, width, 14, 6);
        frame.RequestedTheme = _root.RequestedTheme;
        frame.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        var origin = anchor.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(0, 0));
        var x = origin.X + anchor.ActualWidth - width + Ui.D(4);
        double y;
        if (search) y = origin.Y + anchor.ActualHeight + Ui.D(6);
        else y = origin.Y + anchor.ActualHeight / 2 - (Ui.D(6) + 1 + selected * rowH + rowH / 2);   // the chosen item sits over the button
        y = Math.Clamp(y, 8, Math.Max(8, _root.ActualHeight - frame.DesiredSize.Height - 8));
        x = Math.Clamp(x, 8, Math.Max(8, _root.ActualWidth - width - 8));
        popup.Child = frame;
        popup.HorizontalOffset = x;
        popup.VerticalOffset = y;
        if (box != null) popup.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        popup.IsOpen = true;
        Anim.PopIn(frame, 0, -6, 140);
    }

    // ---------------------------------------------------------------- line height slider (1.00 to 2.00)

    private static UIElement LineSlider(double value, Action<double> changed)
    {
        var width = Ui.D(180);
        var thumbSize = Ui.D(26);
        var frac = Math.Clamp(value - 1.0, 0, 1);
        var valueText = NoWrap(Ui.T($"{value:0.00}\u00D7", 14, Br.Faint));
        valueText.MinWidth = Ui.D(46);
        valueText.TextAlignment = TextAlignment.Right;

        var host = new Grid { Width = width, Height = thumbSize, Background = Br.Clear };
        host.Children.Add(new Border { Height = Ui.D(6), CornerRadius = new CornerRadius(Ui.D(3)), Background = Br.ToggleOff, VerticalAlignment = VerticalAlignment.Center });
        var fill = new Border { Height = Ui.D(6), CornerRadius = new CornerRadius(Ui.D(3)), Background = Br.Accent, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        var thumb = new Border
        {
            Width = thumbSize, Height = thumbSize, CornerRadius = new CornerRadius(thumbSize / 2), Background = Br.White,
            BorderBrush = Br.CardEdge, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left
        };
        host.Children.Add(fill);
        host.Children.Add(thumb);

        void Place()
        {
            var left = frac * (width - thumbSize);
            thumb.Margin = new Thickness(left, 0, 0, 0);
            fill.Width = left + thumbSize / 2;
        }
        Place();

        var dragging = false;
        void Set(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var x = e.GetCurrentPoint(host).Position.X;
            frac = Math.Clamp((x - thumbSize / 2) / (width - thumbSize), 0, 1);
            var v = Math.Round(1.0 + frac, 2);
            valueText.Text = $"{v:0.00}\u00D7";
            Place();
            changed(v);
        }
        host.PointerPressed += (_, e) => { dragging = host.CapturePointer(e.Pointer); if (dragging) Set(e); };
        host.PointerMoved += (_, e) => { if (dragging) Set(e); };
        host.PointerReleased += (_, e) => { dragging = false; host.ReleasePointerCapture(e.Pointer); };
        host.PointerCaptureLost += (_, _) => dragging = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.D(14) };
        row.Children.Add(host);
        row.Children.Add(valueText);
        return row;
    }

    private void ShellPage(StackPanel page)
    {
        var installed = Shells.All.Where(s => s.IsInstalled).ToArray();
        var index = Math.Max(0, Array.FindIndex(installed, s => s.Id == S.DefaultShell));
        Section(page, "Shell",
            Row("Default shell", "Used by Open Folder when no session is open. The plus button lets you choose each time. PowerShell 7 and Linux distributions (WSL) show only when installed.",
                Seg(installed.Select(s => s.MenuName).ToArray(), index, i => { S.DefaultShell = installed[i].Id; AppState.Notify(); })),
            Row("Scrollback", "Lines each session keeps.",
                Seg(new[] { "1000", "5000", "10000" }, S.Scrollback <= 1000 ? 0 : S.Scrollback >= 10000 ? 2 : 1,
                    i => { S.Scrollback = i == 0 ? 1000 : i == 2 ? 10000 : 5000; AppState.Notify(); })));
        Section(page, "Default terminal",
            Row("Use CMD Workspace as the default terminal",
                "Off by default. When on, Command Prompt and PowerShell windows that Windows would open (Win+R, Explorer, other programs) open here as a new session instead. Windows can change or remove this feature in an update.",
                Elevation.IsElevated ? Tag("Not in an administrator window", Br.Faint) : Toggle(DefaultTerminal.IsOn(), on => _ = DefaultTerminalAsync(on))));
        Note(page, "Turning it on changes two console values for your Windows user only. The old values are saved and come back when you turn it off or uninstall. Consoles started as administrator are not handed over. Resizing a handed over session is not verified yet.");
        Section(page, "Found on this PC",
            Shells.All.Select(s => Row(s.MenuName, s.Exe, Tag(s.IsInstalled ? "Found" : "Not installed", s.IsInstalled ? Br.Dim : Br.Faint))).ToArray());
    }

    private void KeyboardPage(StackPanel page)
    {
        Section(page, "Edit",
            Row("Copy", "Copies only when text is selected. Otherwise Ctrl+C goes to the program.", Chip("Ctrl+C")),
            Row("Paste", "Pasted text is checked first (control characters removed, several lines ask).", Chip("Ctrl+V")));
        Note(page, "These shortcuts are fixed. Changing them and adding more is not built.");
    }

    private void Advanced(StackPanel page)
    {
        Section(page, "Display",
            Row("Text size", "Makes the left bar, menus and terminal text bigger or smaller. 100% is the default.",
                Seg(new[] { "90%", "100%", "110%", "120%", "130%" }, Math.Clamp((int)Math.Round((S.TextScale - 0.9) / 0.1), 0, 4),
                    i => { S.TextScale = Math.Round(0.9 + i * 0.1, 1); AppState.Notify(); })));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.D(10) };
        buttons.Children.Add(ChipButton("Show in File Explorer", () => ShowInExplorer(AppState.FilePath, false)));
        buttons.Children.Add(ChipButton("Open", () => OpenFile(AppState.FilePath)));
        Section(page, "Configuration", Row("Settings file", AppState.FilePath, buttons));
        Section(page, "Data",
            Row("Folder", AppState.Folder, ChipButton("Show in File Explorer", () => ShowInExplorer(AppState.Folder, true))),
            Row("Logs folder", Log.Dir, ChipButton("Show in File Explorer", () => ShowInExplorer(Log.Dir, true))),
            Row("Reset all settings", "Back to how CMD Workspace comes.", ChipButton("Reset...", async () => await ResetAsync())));
        Note(page, "Settings only. Never commands or terminal output.");
    }

    private async Task DefaultTerminalAsync(bool on)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            string? problem = null;
            if (on)
            {
                var ask = new ContentDialog
                {
                    Title = "Use CMD Workspace as the default terminal?",
                    Content = new TextBlock
                    {
                        Text = "This changes two console values for your Windows user. The old values are saved first and come back when you turn this off. While it is on, new Command Prompt and PowerShell windows open in CMD Workspace. You can turn it off here at any time.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = "Turn on", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                    XamlRoot = _root.XamlRoot, RequestedTheme = _root.RequestedTheme
                };
                if (await ask.ShowAsync() == ContentDialogResult.Primary)
                {
                    problem = DefaultTerminal.Enable();
                    if (problem == null) DefaultTerminal.StartServer();
                }
            }
            else
            {
                DefaultTerminal.StopServer();
                problem = DefaultTerminal.Disable();
            }
            if (problem != null)
            {
                await new ContentDialog
                {
                    Title = on ? "Not turned on" : "Not fully turned off",
                    Content = new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "Close", XamlRoot = _root.XamlRoot, RequestedTheme = _root.RequestedTheme
                }.ShowAsync();
            }
        }
        catch (Exception ex) { Log.Error("default terminal switch", ex); }
        finally
        {
            _dialogOpen = false;
            RenderPane();   // the switch shows what Windows really has now
        }
    }

    private async Task ResetAsync()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Reset all settings?",
                Content = new TextBlock { Text = "Theme, text size, terminal and shell settings go back to how CMD Workspace comes. Open sessions stay open.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Reset", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                XamlRoot = _root.XamlRoot, RequestedTheme = _root.RequestedTheme
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) AppState.ResetAll();
        }
        finally { _dialogOpen = false; }
    }

    private static void ShowInExplorer(string path, bool folder)
    {
        try
        {
            if (folder) Directory.CreateDirectory(path);
            else if (!File.Exists(path)) AppState.Save();
            var args = folder ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("show in explorer", ex); }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) AppState.Save();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open file", ex); }
    }

    // ---------------------------------------------------------------- building blocks (sizes from the mockup)

    private static void Header(StackPanel page, string title)
    {
        var h = Ui.T(title, 15, Br.Dim, semi: true);
        h.Height = Ui.D(20);
        h.Margin = new Thickness(Ui.D(5), 0, 0, Ui.D(10));
        page.Children.Add(h);
    }

    private static void Note(StackPanel page, string text)
    {
        var n = Ui.T(text, 13, Br.Faint);
        n.TextWrapping = TextWrapping.Wrap;
        n.TextTrimming = TextTrimming.None;
        n.Margin = new Thickness(Ui.D(5), 0, 0, Ui.D(24));
        page.Children.Add(n);
    }

    private static void Section(StackPanel page, string title, params UIElement[] rows)
    {
        if (title.Length > 0) Header(page, title);
        var list = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) list.Children.Add(new Border { Height = 1, Background = Br.Divider, Margin = new Thickness(Ui.D(20), 0, Ui.D(20), 0) });
            list.Children.Add(rows[i]);
        }
        page.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Ui.D(14)), Background = Br.Group, Child = list,
            Margin = new Thickness(0, 0, 0, Ui.D(30))
        });
    }

    private static UIElement Row(string label, string? description, UIElement control)
    {
        var grid = new Grid { Padding = new Thickness(Ui.D(20), Ui.D(15), Ui.D(20), Ui.D(15)), ColumnSpacing = Ui.D(24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var name = Ui.T(label, 16, Br.Text);
        name.TextWrapping = TextWrapping.Wrap;
        name.TextTrimming = TextTrimming.None;
        left.Children.Add(name);
        if (!string.IsNullOrEmpty(description))
        {
            var d = Ui.T(description, 14, Br.Dim);
            d.TextWrapping = TextWrapping.Wrap;
            d.TextTrimming = TextTrimming.None;
            d.Margin = new Thickness(0, Ui.D(5), 0, 0);
            left.Children.Add(d);
        }
        grid.Children.Add(left);
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.HorizontalAlignment = HorizontalAlignment.Right;
        }
        Grid.SetColumn((FrameworkElement)control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static UIElement Seg(string[] labels, int selected, Action<int> pick)
    {
        var current = selected;
        var parts = new List<(SolidColorBrush Bg, SolidColorBrush Edge, SolidColorBrush Text)>();
        var strip = new StackPanel { Orientation = Orientation.Horizontal };

        void Paint(bool animate)
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var on = i == current;
                var bg = on ? Br.SegSel.Color : Windows.UI.Color.FromArgb(0, Br.SegSel.Color.R, Br.SegSel.Color.G, Br.SegSel.Color.B);
                var edge = on ? Br.SegSelEdge.Color : Windows.UI.Color.FromArgb(0, Br.SegSelEdge.Color.R, Br.SegSelEdge.Color.G, Br.SegSelEdge.Color.B);
                var text = on ? Br.Text.Color : Br.Dim.Color;
                if (animate)
                {
                    Anim.ColorTo(parts[i].Bg, bg, 140);
                    Anim.ColorTo(parts[i].Edge, edge, 140);
                    Anim.ColorTo(parts[i].Text, text, 140);
                }
                else
                {
                    parts[i].Bg.Color = bg;
                    parts[i].Edge.Color = edge;
                    parts[i].Text.Color = text;
                }
            }
        }

        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var textBrush = new SolidColorBrush();
            var label = NoWrap(Ui.T(labels[i], 15, textBrush));
            var bgBrush = new SolidColorBrush();
            var edgeBrush = new SolidColorBrush();
            var inner = new Border
            {
                CornerRadius = new CornerRadius(Ui.D(7)), Padding = new Thickness(Ui.D(14), Ui.D(6), Ui.D(14), Ui.D(6)),
                Background = bgBrush, BorderBrush = edgeBrush, BorderThickness = new Thickness(1), Child = label
            };
            parts.Add((bgBrush, edgeBrush, textBrush));
            var button = Ui.Flat(inner, labels[i], Ui.D(7));
            button.Click += (_, _) => { if (index == current) return; current = index; Paint(true); pick(index); };
            strip.Children.Add(button);
        }
        Paint(false);
        return new Border { Background = Br.Seg, CornerRadius = new CornerRadius(Ui.D(9)), Padding = new Thickness(Ui.D(3)), Child = strip };
    }

    private static TextBlock NoWrap(TextBlock t)
    {
        t.TextWrapping = TextWrapping.NoWrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    private static UIElement Toggle(bool on, Action<bool> changed)
    {
        var state = on;
        var slide = new TranslateTransform();
        var knob = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = Ui.D(20), Height = Ui.D(20), Fill = Br.White, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(Ui.D(3), 0, 0, 0), RenderTransform = slide
        };
        var trackFill = new SolidColorBrush((state ? Br.ToggleOn : Br.ToggleOff).Color);
        var track = new Grid { Width = Ui.D(46), Height = Ui.D(26), CornerRadius = new CornerRadius(Ui.D(13)), Background = trackFill };
        track.Children.Add(knob);
        var button = Ui.Flat(track, on ? "On" : "Off", Ui.D(13));
        slide.X = state ? Ui.D(20) : 0;

        button.Click += (_, _) =>
        {
            state = !state;
            Anim.SlideX(slide, state ? Ui.D(20) : 0, 150);
            Anim.ColorTo(trackFill, (state ? Br.ToggleOn : Br.ToggleOff).Color, 150);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, state ? "On" : "Off");
            changed(state);
        };
        return button;
    }

    private static UIElement ChipButton(string text, Action click)
    {
        var inner = new Border
        {
            CornerRadius = new CornerRadius(Ui.D(8)), Background = Br.SegSel, BorderBrush = Br.SegSelEdge, BorderThickness = new Thickness(1),
            Padding = new Thickness(Ui.D(16), Ui.D(7), Ui.D(16), Ui.D(7)), Child = NoWrap(Ui.T(text, 15, Br.Text))
        };
        var button = Ui.Flat(inner, text, Ui.D(8));
        button.Click += (_, _) => click();
        return button;
    }

    private static UIElement Stepper(string value, Action minus, Action plus)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.D(10) };
        strip.Children.Add(ChipButton("-", minus));
        var v = NoWrap(Ui.T(value, 15, Br.Text));
        v.MinWidth = Ui.D(40);
        v.TextAlignment = TextAlignment.Center;
        strip.Children.Add(v);
        strip.Children.Add(ChipButton("+", plus));
        return strip;
    }

    private static UIElement Chip(string text) => new Border
    {
        MinWidth = Ui.D(84), CornerRadius = new CornerRadius(Ui.D(7)), Background = Br.SegSel, BorderBrush = Br.SegSelEdge, BorderThickness = new Thickness(1),
        Padding = new Thickness(Ui.D(12), Ui.D(5), Ui.D(12), Ui.D(5)),
        Child = new TextBlock { Text = text, FontSize = Ui.F(14), Foreground = Br.Text, HorizontalAlignment = HorizontalAlignment.Center }
    };

    private static UIElement Tag(string text, Brush brush) => Ui.T(text, 14, brush);
}
