using System.Diagnostics;
using CmdWorkspace.Core;
using CmdWorkspace.Term;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CmdWorkspace;

// Menus, search panel, notices and dialogs. All pop-ups use Popup or ContentDialog, which draw above the terminal web view.
public sealed partial class MainWindow
{
    private Popup? _menu;
    private Popup? _toast;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _toastWired;

    private Popup Open(FrameworkElement content, double x, double y, bool lightDismiss = true, double slide = -6)
    {
        content.RequestedTheme = Root.RequestedTheme;
        var popup = new Popup
        {
            XamlRoot = Root.XamlRoot,
            Child = content,
            HorizontalOffset = x,
            VerticalOffset = y,
            IsLightDismissEnabled = lightDismiss
        };
        popup.IsOpen = true;
        Anim.PopIn(content, 0, slide, 140);
        return popup;
    }

    private void CloseMenu()
    {
        var popup = _menu;
        _menu = null;
        CloseAnimated(popup, -4);
    }

    /// <summary>Closes a popup with a short fade and slide. Without animations it closes at once. Light dismiss (click outside) is closed by Windows itself and stays instant.</summary>
    private static void CloseAnimated(Popup? popup, double slide)
    {
        if (popup == null || !popup.IsOpen) return;
        if (popup.Child is FrameworkElement box && Anim.Enabled)
        {
            box.IsHitTestVisible = false;
            Anim.PopOut(box, 0, slide, 110, () => popup.IsOpen = false);
        }
        else popup.IsOpen = false;
    }

    private static double CardLeft => Ui.D(AppState.S.BarOpen ? 335 : 10);

    private Button MenuRow(string text, Action run, double heightPx = 38, double textPx = 15, FrameworkElement? leading = null,
        string? right = null, Brush? rightBrush = null, Brush? textBrush = null)
    {
        var grid = new Grid { Margin = new Thickness(Ui.D(12), 0, Ui.D(12), 0), ColumnSpacing = Ui.D(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (leading != null) grid.Children.Add(leading);
        var label = Ui.T(text, textPx, textBrush ?? Br.Text);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (right != null)
        {
            var r = Ui.T(right, textPx, rightBrush ?? Br.Faint);
            Grid.SetColumn(r, 2);
            grid.Children.Add(r);
        }
        var button = Ui.Flat(grid, text, Ui.D(8), double.NaN, Ui.D(heightPx));
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Click += (_, _) => { CloseMenu(); run(); };
        return button;
    }

    private static TextBlock SmallLabel(string text)
    {
        var t = Ui.T(text, 12, Br.Faint);
        t.CharacterSpacing = 60;
        t.Margin = new Thickness(Ui.D(12), Ui.D(6), Ui.D(12), Ui.D(4));
        return t;
    }

    // ---------------------------------------------------------------- plus menu

    private bool _plusMenuBusy;

    // Linux distributions (WSL) are looked up first (cached for 30 seconds, at most about 2.5 seconds wait), so a distribution installed later shows up by itself.
    private async void ShowPlusMenu()
    {
        if (_plusMenuBusy) return;
        _plusMenuBusy = true;
        try { await Shells.RefreshWslAsync(); }
        finally { _plusMenuBusy = false; }
        CloseMenu();
        var stack = new StackPanel();
        foreach (var shell in Shells.All.Where(s => s.IsInstalled))
        {
            var s = shell;
            stack.Children.Add(MenuRow(s.MenuName, () => _ = NewSessionAsync(s)));
        }
        if (!Elevation.IsElevated)
        {
            stack.Children.Add(Ui.Divider());
            stack.Children.Add(MenuRow("Open CMD as Administrator", () => OpenAsAdmin(Shells.Cmd)));
            stack.Children.Add(MenuRow("Open PowerShell as Administrator", () => OpenAsAdmin(Shells.WindowsPowerShell)));
        }
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(MenuRow("Open Folder...", () => _ = PickFolderAsync()));
        _menu = Open(Ui.PopBox(stack, Ui.D(262)), Ui.D(56), Ui.D(58));
    }

    private void OpenAsAdmin(ShellInfo shell)
    {
        var problem = Elevation.LaunchAsAdmin(shell.Id);
        if (problem != null) Toast("Administrator window not opened", problem);
    }

    // ---------------------------------------------------------------- session row menu (right click)

    private void ShowRowMenu(Session session, double x, double y)
    {
        CloseMenu();
        var stack = new StackPanel();
        stack.Children.Add(MenuRow("Rename", () => _ = RenameAsync(session)));
        stack.Children.Add(MenuRow("Move to Project", () => ShowMoveMenu(session, x, y), right: ">"));
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(MenuRow("Copy Session ID", () => CopyText(session.Id.ToString(), "Session ID copied")));
        stack.Children.Add(MenuRow("Copy Directory", () => CopyText(session.Folder, "Directory copied")));
        stack.Children.Add(MenuRow("Reveal in File Explorer", () => RevealFolder(session.Folder)));
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(MenuRow("Close Session", () => _ = CloseSessionAsync(session)));
        var box = Ui.PopBox(stack, Ui.D(250));
        box.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var px = Math.Max(8, Math.Min(x, Root.ActualWidth - box.Width - 8));
        var py = Math.Max(8, Math.Min(y, Root.ActualHeight - box.DesiredSize.Height - 8));
        _menu = Open(box, px, py);
    }

    // Moving only changes the group in the left bar. The shell keeps running in its own folder.
    private void ShowMoveMenu(Session session, double x, double y)
    {
        CloseMenu();
        var stack = new StackPanel();
        stack.Children.Add(SmallLabel("MOVE TO PROJECT"));
        var current = ProjectOf(session);
        var projects = ProjectStore.Data.Projects;
        if (projects.Count == 0)
        {
            var none = Ui.T("No projects saved yet.", 14, Br.Dim);
            none.Margin = new Thickness(Ui.D(12), Ui.D(6), Ui.D(12), Ui.D(8));
            stack.Children.Add(none);
        }
        foreach (var project in projects.ToList())
        {
            var p = project;
            var isCurrent = current?.Id == p.Id;
            FrameworkElement lead = isCurrent ? Ui.Icon(Icons.Check, Ui.D(18), Br.Accent) : new Border { Width = Ui.D(18), Height = Ui.D(18) };
            stack.Children.Add(MenuRow(p.Name, () =>
            {
                session.ProjectId = p.Id;
                ProjectStore.SetLastProject(p.Id);
                RenderBar();
                Toast("Moved to " + p.Name, "Only the group changed. The shell is still in " + session.Folder);
            }, leading: lead));
        }
        stack.Children.Add(Ui.Divider());
        if (ProjectStore.ByPath(session.Folder) == null)
        {
            stack.Children.Add(MenuRow("Save this folder as a project", () =>
            {
                var created = ProjectStore.Add(session.Folder, null, out var problem);
                if (created == null) { Toast("Not saved", problem); return; }
                session.ProjectId = created.Id;
                ProjectStore.SetLastProject(created.Id);
                RenderBar();
                Toast("Saved as project " + created.Name, session.Folder);
            }));
        }
        if (session.ProjectId != null && ProjectStore.ByPath(session.Folder)?.Id != session.ProjectId)
        {
            stack.Children.Add(MenuRow("Back to its own folder", () => { session.ProjectId = null; RenderBar(); }));
        }
        var box = Ui.PopBox(new ScrollViewer { Content = stack, MaxHeight = Math.Max(160, Root.ActualHeight - 80), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Ui.D(300));
        box.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var px = Math.Max(8, Math.Min(x, Root.ActualWidth - box.Width - 8));
        var py = Math.Max(8, Math.Min(y, Root.ActualHeight - box.DesiredSize.Height - 8));
        _menu = Open(box, px, py);
    }

    private async Task RenameAsync(Session session)
    {
        var box = new TextBox { Text = RowName(session), PlaceholderText = "Session name", SelectionStart = 0 };
        box.SelectAll();
        if (!await ShowDialog("Rename session", box, "Rename")) return;
        var text = box.Text.Trim();
        session.CustomName = text.Length == 0 ? null : text;
        RenderBar();
    }

    private void CopyText(string text, string message)
    {
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            Toast(message);
        }
        catch (Exception ex) { Log.Write("copy failed: " + ex.Message); Toast("Could not copy", ex.Message); }
    }

    private void RevealFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) { Toast("Folder not available", folder); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("reveal failed: " + ex.Message); Toast("Could not open Explorer", ex.Message); }
    }

    // ---------------------------------------------------------------- folder menu

    private bool _folderMenuBusy;

    // Projects and recent folders are checked first (at most about one second), so a missing folder is marked honestly.
    private async void ShowFolderMenu()
    {
        if (_folderMenuBusy) return;
        _folderMenuBusy = true;
        try
        {
            CloseMenu();
            var data = ProjectStore.Data;
            var states = await Folders.CheckManyAsync(data.Projects.Select(p => p.Path).Concat(data.Recent.Select(r => r.Path)));
            FolderState StateOf(string path) => states.TryGetValue(path, out var st) ? st : FolderState.Ok;

            var stack = new StackPanel();
            stack.Children.Add(MenuRow("Open Folder...", () => _ = PickFolderAsync(), leading: Ui.Icon(Icons.Folder, Ui.D(18), Br.Accent)));
            if (data.Projects.Count > 0)
            {
                stack.Children.Add(Ui.Divider());
                stack.Children.Add(SmallLabel("PROJECTS"));
                foreach (var project in data.Projects.ToList())
                {
                    var p = project;
                    var st = StateOf(p.Path);
                    var bad = st != FolderState.Ok;
                    stack.Children.Add(MenuRow(p.Name, () => _ = OpenFolderAsync(p.Path), 36, 14, null,
                        bad ? Folders.Tag(st) : null, Br.Red, bad ? Br.Faint : Br.Text));
                }
            }
            if (data.Recent.Count > 0)
            {
                stack.Children.Add(Ui.Divider());
                stack.Children.Add(SmallLabel("RECENT"));
                foreach (var recent in data.Recent.ToList())
                {
                    var p = recent.Path;
                    var st = StateOf(p);
                    var bad = st != FolderState.Ok;
                    stack.Children.Add(MenuRow(p, () => _ = OpenFolderAsync(p), 36, 14, null,
                        bad ? Folders.Tag(st) : Ago(recent.When), bad ? Br.Red : Br.Faint, bad ? Br.Faint : Br.Text));
                }
            }
            var scroll = new ScrollViewer { Content = stack, MaxHeight = Math.Max(160, Root.ActualHeight - 120), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var box = Ui.PopBox(scroll, Ui.D(340));
            box.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var y = Math.Max(8, Root.ActualHeight - 4 - 32 - 8 - box.DesiredSize.Height);
            CloseMenu();
            _menu = Open(box, Ui.D(14), y);
        }
        finally { _folderMenuBusy = false; }
    }

    // ---------------------------------------------------------------- search panel

    private void ShowSearch()
    {
        CloseMenu();
        var width = Ui.D(600);
        var cardWidth = Root.ActualWidth - CardLeft - Ui.D(11);
        var results = new StackPanel { Margin = new Thickness(Ui.D(8)) };
        var actions = new List<Action>();

        var box = new TextBox
        {
            PlaceholderText = "Search sessions, projects and recent folders",
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = Br.Text,
            FontSize = Ui.F(16),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                                    "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);

        var header = new Grid { Height = Ui.D(56), Padding = new Thickness(Ui.D(20), 0, Ui.D(20), 0), ColumnSpacing = Ui.D(12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(Ui.Icon(Icons.Search, Ui.D(20), Br.Dim));
        Grid.SetColumn(box, 1);
        header.Children.Add(box);
        var line = new Border { Height = 1, Background = Br.Divider };

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(line);
        panel.Children.Add(results);
        var frame = Ui.PopBox(panel, width, 16, 0);
        frame.MaxHeight = Math.Max(200, Root.ActualHeight - Ui.D(96) - 20);

        void Refill()
        {
            results.Children.Clear();
            actions.Clear();
            var q = box.Text.Trim();
            bool Match(string s) => q.Length == 0 || s.Contains(q, StringComparison.CurrentCultureIgnoreCase);

            var sessions = _sessions.Sessions.Where(s => Match(RowName(s)) || Match(s.Folder)).ToList();
            if (sessions.Count > 0)
            {
                results.Children.Add(SmallLabel("SESSIONS"));
                foreach (var s in sessions)
                {
                    var session = s;
                    results.Children.Add(MenuRow(RowName(s), () => Activate(session), 38, 15, null, GroupName(s.Folder)));
                    actions.Add(() => Activate(session));
                }
            }
            var projects = ProjectStore.Data.Projects.Where(p => Match(p.Name) || Match(p.Path)).ToList();
            if (projects.Count > 0)
            {
                results.Children.Add(SmallLabel("PROJECTS"));
                foreach (var project in projects)
                {
                    var p = project.Path;
                    results.Children.Add(MenuRow(project.Name, () => _ = OpenFolderAsync(p), 38, 15, null, p));
                    actions.Add(() => _ = OpenFolderAsync(p));
                }
            }
            var folders = ProjectStore.Data.Recent.Where(r => Match(r.Path)).ToList();
            if (folders.Count > 0)
            {
                results.Children.Add(SmallLabel("RECENT FOLDERS"));
                foreach (var recent in folders)
                {
                    var p = recent.Path;
                    results.Children.Add(MenuRow(p, () => _ = OpenFolderAsync(p), 38, 15, null, Ago(recent.When)));
                    actions.Add(() => _ = OpenFolderAsync(p));
                }
            }
            if (actions.Count == 0)
            {
                var none = Ui.T("Nothing found. Search covers session names, saved projects and recent folders.", 14, Br.Dim);
                none.TextWrapping = TextWrapping.Wrap;
                none.Margin = new Thickness(Ui.D(12), Ui.D(10), Ui.D(12), Ui.D(10));
                results.Children.Add(none);
            }
        }

        box.TextChanged += (_, _) => Refill();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { CloseMenu(); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Enter && actions.Count > 0)
            {
                var run = actions[0];
                CloseMenu();
                run();
                e.Handled = true;
            }
        };
        Refill();
        _menu = Open(frame, CardLeft + Math.Max(0, (cardWidth - width) / 2), Ui.D(96));
        DispatcherQueue.TryEnqueue(() => box.Focus(FocusState.Programmatic));
    }

    // ---------------------------------------------------------------- notice

    private void Toast(string title, string? sub = null)
    {
        if (Root.XamlRoot == null) return;
        if (!_toastWired)
        {
            _toastWired = true;
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); var old = _toast; _toast = null; CloseAnimated(old, -4); };
        }
        { var old = _toast; _toast = null; CloseAnimated(old, -4); }

        var stack = new StackPanel { Spacing = 2 };
        var head = Ui.T(title, 15, Br.Text, semi: true);
        head.TextWrapping = TextWrapping.Wrap;
        head.TextTrimming = TextTrimming.None;
        stack.Children.Add(head);
        if (!string.IsNullOrEmpty(sub))
        {
            var body = Ui.T(sub, 14, Br.Dim);
            body.TextWrapping = TextWrapping.Wrap;
            body.TextTrimming = TextTrimming.None;
            stack.Children.Add(body);
        }
        var box = Ui.PopBox(stack, Ui.D(520), 12, 12);
        box.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var cardWidth = Root.ActualWidth - CardLeft - Ui.D(11);
        var x = CardLeft + Math.Max(0, (cardWidth - box.Width) / 2);
        var y = Math.Max(8, Root.ActualHeight - Ui.D(10) - 24 - box.DesiredSize.Height);
        _toast = Open(box, x, y, lightDismiss: false, slide: 10);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- dialogs

    private async Task<bool> Confirm(string title, string body, string primary)
    {
        var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap };
        return await ShowDialog(title, text, primary);
    }

    private async Task<bool> ConfirmPaste(string pasted)
    {
        var lines = pasted.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var preview = string.Join("\n", lines.Take(6).Select(l => l.Length > 90 ? l[..90] + "..." : l));
        if (lines.Length > 6) preview += "\n...";
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = lines.Length + " lines will be pasted. Nothing runs by itself, and no Enter is added.",
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock { Text = preview, FontFamily = new FontFamily(Ui.MonoFamily), FontSize = 12, Opacity = 0.8 });
        return await ShowDialog("Paste several lines?", stack, "Paste");
    }

    private async Task<bool> ShowDialog(string title, object content, string primary)
    {
        if (_dialogOpen || Root.XamlRoot == null) return false;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primary,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.RequestedTheme
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _dialogOpen = false; }
    }
}
