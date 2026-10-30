using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace CmdWorkspace.Core;

/// <summary>
/// One normal window (Phase 11b). The first normal window owns a mutex and listens on a pipe that only the same Windows user can open.
/// A second start (cws, the right click item, the Start menu) sends the folder to the first window and exits. The admin window is never part of this.
/// Start with --new-window to get a separate normal window. If the hand over fails for any reason, the second start simply opens as before.
/// The message is one line: ACTIVATE, or FOLDER and a tab and a path. Nothing is run from it, the folder is checked like any chosen folder.
/// </summary>
public static class SingleInstance
{
    public const string NewWindow = "--new-window";
    private const int MaxLine = 4096;
    private static Mutex? _mutex;
    private static bool _owner;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static string Sid
    {
        get
        {
            try { using var id = WindowsIdentity.GetCurrent(); return id.User?.Value ?? Environment.UserName; }
            catch { return Environment.UserName; }
        }
    }

    private static string MutexName => "Local\\CMDWorkspace-" + Sid;
    private static string PipeName => "CMDWorkspace-" + Sid;

    /// <summary>True when the request was given to the running window and this process should exit. False means: start normally.</summary>
    public static bool TryHandOver(string? folder)
    {
        try
        {
            if (Elevation.IsElevated) return false;
            if (Environment.GetCommandLineArgs().Any(a => a.Equals(NewWindow, StringComparison.OrdinalIgnoreCase))) return false;

            _mutex = new Mutex(true, MutexName, out var created);
            if (created) { _owner = true; return false; }   // first window
            _mutex.Dispose();
            _mutex = null;
            // Windows started this program as the default terminal (-Embedding): it never hands over, it is its own window.
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("-Embedding", StringComparison.OrdinalIgnoreCase))) return false;

            AllowSetForegroundWindow(-1);                    // the running window may come to the front
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var line = folder == null ? "ACTIVATE" : "FOLDER\t" + folder;
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            Log.Write("handed over to the running window: " + (folder ?? "(bring to front)"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("single instance hand over (starting a normal window instead)", ex);
            // The old window may be closing. If its mutex is gone now, this window becomes the first one.
            try
            {
                if (_mutex == null)
                {
                    _mutex = new Mutex(true, MutexName, out var created);
                    _owner = created;
                }
            }
            catch { }
            return false;
        }
    }

    /// <summary>Starts listening (first window only). onRequest gets null (bring to front) or a folder path. It runs on a background thread.</summary>
    public static void StartServer(Action<string?> onRequest)
    {
        if (!_owner) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();
                    using var cts = new CancellationTokenSource(2000);
                    var buffer = new byte[MaxLine];
                    var count = 0;
                    while (count < MaxLine)
                    {
                        var n = await server.ReadAsync(buffer.AsMemory(count, MaxLine - count), cts.Token);
                        if (n == 0) break;
                        count += n;
                        if (Array.IndexOf(buffer, (byte)'\n', 0, count) >= 0) break;
                    }
                    Handle(Encoding.UTF8.GetString(buffer, 0, count), onRequest);
                }
                catch (Exception ex)
                {
                    Log.Error("single instance listener", ex);
                    await Task.Delay(500);
                }
            }
        });
    }

    private static void Handle(string text, Action<string?> onRequest)
    {
        var line = text.Split('\n')[0].Trim('\r', ' ');
        if (line == "ACTIVATE") { onRequest(null); return; }
        const string prefix = "FOLDER\t";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) { Log.Write("single instance: message ignored"); return; }
        var path = line[prefix.Length..].Trim();
        if (path.Length == 0 || path.Length > 1024 || path.Any(char.IsControl) || !Path.IsPathRooted(path)) { Log.Write("single instance: folder ignored"); return; }
        onRequest(path);
    }
}
