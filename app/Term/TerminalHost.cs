using System.Text;
using System.Text.Json;
using CmdWorkspace.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace CmdWorkspace.Term;

/// <summary>
/// The terminal screen: one WebView2 with xterm.js, one xterm per session (decision D2).
/// It owns no process. Terminal text is untrusted: a link is only REPORTED here, the owner checks it before opening anything.
/// Clipboard text is read only for a paste the user asked for, and is never logged or stored.
/// </summary>
public sealed class TerminalHost : UserControl
{
    private readonly WebView2 _web = new();
    private readonly Dictionary<int, Session> _sessions = new();
    private readonly List<string> _pending = new();
    private readonly Dictionary<int, StringBuilder> _buffers = new();
    private bool _ready, _started, _flushQueued;

    /// <summary>True once the terminal page has loaded. Until then the web view is kept hidden (it would show a black square).</summary>
    public bool IsReady => _ready;

    /// <summary>The terminal page finished loading.</summary>
    public event Action? Ready;

    public short Cols { get; private set; } = 120;
    public short Rows { get; private set; } = 30;

    /// <summary>The user clicked a link. The text is NOT checked yet.</summary>
    public event Action<Session, string>? LinkClicked;

    /// <summary>The user wants to paste text with more than one line.</summary>
    public event Action<Session, string>? MultilinePaste;

    /// <summary>A short message for the user (no terminal text, no clipboard text).</summary>
    public event Action<string>? Info;

    public TerminalHost()
    {
        Content = _web;
        Loaded += async (_, _) => await StartAsync();
    }

    /// <summary>A picture of what the terminal shows now, or null. Used behind the dimmed Settings panel (a web view cannot be dimmed itself).</summary>
    public async Task<Microsoft.UI.Xaml.Media.ImageSource?> CaptureAsync()
    {
        try
        {
            if (!_ready || _web.CoreWebView2 == null || Visibility != Microsoft.UI.Xaml.Visibility.Visible) return null;
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Seek(0);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("terminal snapshot", ex);
            return null;
        }
    }

    private async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            var webDir = Path.Combine(AppContext.BaseDirectory, "Web");
            if (!File.Exists(Path.Combine(webDir, "vendor", "xterm.js")))
            {
                Info?.Invoke("The terminal files are missing. Close the app and run start.cmd again.");
                Log.Write("xterm.js not found in " + webDir);
                return;
            }
            await _web.EnsureCoreWebView2Async();
            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("terminal.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("https://terminal.local/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            // Phase 7: keep browser shortcuts (F5 reload, print, browser zoom) away from the terminal page. Untested, see HANDOFF.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://terminal.local/index.html");
        }
        catch (Exception ex)
        {
            Log.Error("terminal start", ex);
            Info?.Invoke("The terminal screen could not start: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- to the page

    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message);
        if (!_ready) { _pending.Add(json); return; }
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }

    /// <summary>Sends colours, font and cursor settings to every terminal.</summary>
    public void Configure()
    {
        var s = AppState.S;
        var p = s.Theme == "Dark" ? Palette.Dark : Palette.Light;
        Post(new
        {
            t = "cfg",
            dark = p.IsDark,
            bg = Palette.Css(p.Card),
            fg = Palette.Css(p.Text),
            cursor = Palette.Css(p.Cursor),
            sel = Palette.Css(p.TermSel),
            ansi = p.Ansi,
            pop = Palette.Css(p.Pop),
            popEdge = Palette.Css(p.PopEdge),
            hover = Palette.Css(p.Hover),
            fontSize = Math.Round(s.FontSize * s.TextScale),
            fontFamily = Fonts.CssList(s.FontFamily),
            fontName = Fonts.Get(s.FontFamily).Css[0],
            weight = Fonts.WeightNumbers(s.Weight).Normal,
            weightBold = Fonts.WeightNumbers(s.Weight).Bold,
            ligatures = s.Ligatures,
            thicken = s.Thicken,
            copyOnSelect = s.CopyOnSelect,
            rightClick = s.RightClick,
            lineHeight = s.LineHeight,
            cursorStyle = s.CursorStyle,
            cursorBlink = s.CursorBlink,
            scrollback = s.Scrollback,
            copyMenuOn = s.ShowCopyMenu,
            pasteMenuOn = s.ShowPasteMenu,
            scrollPastEnd = s.ScrollPastEnd
        });
    }

    public void Add(Session session)
    {
        _sessions[session.Id] = session;
        Post(new { t = "create", id = session.Id });
        session.Output += OnOutput;
        session.BeginRead();
    }

    /// <summary>While on, the page does not resize the terminal (the card is sliding). When off, it resizes once.</summary>
    public void Freeze(bool on) => Post(new { t = "freeze", on });

    public void Show(Session session) => Post(new { t = "show", id = session.Id });

    public void Remove(Session session)
    {
        session.Output -= OnOutput;
        _sessions.Remove(session.Id);
        Post(new { t = "remove", id = session.Id });
    }

    /// <summary>
    /// Puts the keyboard in the terminal. The web view itself must get focus (not only the page inside it),
    /// and a menu that just closed may take focus back, so it is repeated a moment later.
    /// </summary>
    public void Focus()
    {
        FocusNow();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusNow);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => FocusNow();
        timer.Start();
    }

    private void FocusNow()
    {
        if (Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;   // hidden (for example while Settings is open): do not take focus
        _web.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        Post(new { t = "focus" });
    }

    /// <summary>Pastes text as a paste (no Enter is added).</summary>
    public void PasteText(Session session, string text) => Post(new { t = "pastetext", id = session.Id, d = text });

    // Output arrives on a background thread in small pieces. It is gathered and sent in one message per UI turn.
    private void OnOutput(Session session, string text)
    {
        bool schedule;
        lock (_buffers)
        {
            if (!_buffers.TryGetValue(session.Id, out var sb)) _buffers[session.Id] = sb = new StringBuilder();
            sb.Append(text);
            schedule = !_flushQueued;
            _flushQueued = true;
        }
        if (schedule) DispatcherQueue.TryEnqueue(Flush);
    }

    private void Flush()
    {
        List<(int Id, string Text)> items;
        lock (_buffers)
        {
            items = _buffers.Select(kv => (kv.Key, kv.Value.ToString())).ToList();
            _buffers.Clear();
            _flushQueued = false;
        }
        foreach (var (id, text) in items)
            if (_sessions.ContainsKey(id)) Post(new { t = "out", id, d = text });
    }

    // ---------------------------------------------------------------- from the page

    private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Phase 7: only our own page may send messages.
            if (!e.Source.StartsWith("https://terminal.local/", StringComparison.OrdinalIgnoreCase)) return;
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            var kind = root.GetProperty("t").GetString();
            if (kind == "ready")
            {
                _ready = true;
                foreach (var json in _pending) _web.CoreWebView2.PostWebMessageAsJson(json);
                _pending.Clear();
                Configure();
                Ready?.Invoke();
                return;
            }
            if (!root.TryGetProperty("id", out var idProp) || !_sessions.TryGetValue(idProp.GetInt32(), out var session)) return;

            switch (kind)
            {
                case "input":
                    session.Write(root.GetProperty("d").GetString() ?? "");
                    break;
                case "zoom":
                    AppState.S.FontSize = Math.Clamp(Math.Round(AppState.S.FontSize) + root.GetProperty("d").GetInt32(), 9, 28);
                    AppState.Notify();
                    break;
                case "resize":
                    Cols = (short)root.GetProperty("cols").GetInt32();
                    Rows = (short)root.GetProperty("rows").GetInt32();
                    session.Resize(Cols, Rows);
                    break;
                case "link":
                    LinkClicked?.Invoke(session, root.GetProperty("uri").GetString() ?? "");
                    break;
                case "clickoffer":
                    if (ClipboardHasText())
                        Post(new { t = "showPaste", id = session.Id, x = root.GetProperty("x").GetDouble(), y = root.GetProperty("y").GetDouble() });
                    break;
                case "paste":
                    await PasteFromClipboardAsync(session);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("terminal message", ex);
        }
    }

    private static bool ClipboardHasText()
    {
        try { return Clipboard.GetContent().Contains(StandardDataFormats.Text); }
        catch { return false; }
    }

    private async Task PasteFromClipboardAsync(Session session)
    {
        string text;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            text = await content.GetTextAsync();
        }
        catch
        {
            Info?.Invoke("Could not read the clipboard.");
            return;
        }

        // Control characters (such as Escape) are removed, so pasted text cannot send terminal commands.
        text = new string(text.Where(c => c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c)).ToArray());   // Phase 7: also removes C1 controls (U+0080 to U+009F)
        if (text.Length == 0) return;
        if (text.Contains('\n') || text.Contains('\r')) MultilinePaste?.Invoke(session, text);
        else PasteText(session, text);
    }
}
