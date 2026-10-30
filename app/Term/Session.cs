using CmdWorkspace.Core;

namespace CmdWorkspace.Term;

public enum SessionState { Starting, Live, Exited, Failed }

/// <summary>
/// One real terminal session: one shell in one pseudo console. It knows nothing about the screen.
/// A session is either live (the shell process runs) or ended. "Idle" is never guessed.
/// </summary>
public sealed class Session
{
    private static int _next;
    private Pty? _pty;

    public int Id { get; } = Interlocked.Increment(ref _next);
    public ShellInfo Shell { get; }
    public string Folder { get; }
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>Name the user typed with Rename. Null means use the shell or folder name.</summary>
    public string? CustomName { get; set; }
    /// <summary>Set by "Move to Project". Null means the group follows the folder (a saved project with the same folder, else the plain folder).</summary>
    public string? ProjectId { get; set; }
    public SessionState State { get; private set; } = SessionState.Starting;
    public int? ExitCode { get; private set; }
    public string? Problem { get; private set; }

    /// <summary>Raised on a background thread with text from the shell.</summary>
    public event Action<Session, string>? Output;

    /// <summary>Raised on a background thread when the state changes.</summary>
    public event Action<Session>? StateChanged;

    public Session(ShellInfo shell, string folder)
    {
        Shell = shell;
        Folder = folder;
    }

    public void Start(short cols, short rows)
    {
        try
        {
            Hook(Pty.Start(Shell.Exe, Shell.Args, Folder, cols, rows));
            Log.Write($"Session {Id} started: {Shell.Id}, program {Shell.Exe}, arguments {Shell.Args}");
            StateChanged?.Invoke(this);
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            State = SessionState.Failed;
            Log.Error($"start session ({Shell.Id})", ex);
            StateChanged?.Invoke(this);
        }
    }

    private void Hook(Pty pty)
    {
        _pty = pty;
        pty.Output += text => Output?.Invoke(this, text);
        pty.Exited += code =>
        {
            ExitCode = code;
            if (State != SessionState.Failed) State = SessionState.Exited;
            Log.Write($"Session {Id} ({Shell.Id}) ended with exit code {code}");
            StateChanged?.Invoke(this);
        };
        State = SessionState.Live;
    }

    /// <summary>Default terminal (Phase 12b): the console program was started by Windows, this session only shows it.</summary>
    internal void StartAttached(Pty pty)
    {
        Hook(pty);
        Log.Write($"Session {Id} attached (default terminal): {Shell.Id}, process {pty.ProcessId}");
        StateChanged?.Invoke(this);
    }

    /// <summary>Call once the Output handler is attached, so nothing is missed.</summary>
    public void BeginRead() => _pty?.BeginRead();

    public void Write(string text)
    {
        if (State == SessionState.Live) _pty?.Write(text);
    }

    public void Resize(short cols, short rows) => _pty?.Resize(cols, rows);

    /// <summary>True when the shell has a running child program. False does not prove the shell is idle.</summary>
    public bool HasChildren() => State == SessionState.Live && (_pty?.HasChildren() ?? false);

    public void Close()
    {
        var pty = _pty;
        _pty = null;
        pty?.Dispose();
        if (State == SessionState.Live || State == SessionState.Starting) State = SessionState.Exited;
    }
}

/// <summary>
/// Owns the sessions. Choosing a folder ALWAYS makes a new session (decision D3, 2026-10-23):
/// no session is reused and no "cd" is ever typed. Called from the UI thread only.
/// </summary>
public sealed class SessionManager
{
    private readonly List<Session> _list = new();

    public IReadOnlyList<Session> Sessions => _list;

    /// <summary>Starts a session, or returns null with the reason in <paramref name="problem"/>.</summary>
    public Session? Create(ShellInfo shell, string folder, short cols, short rows, out string? problem)
    {
        problem = null;
        if (!shell.IsInstalled) { problem = shell.MenuName + " is not installed on this PC."; return null; }
        if (!Directory.Exists(folder)) { problem = "This folder is not available: " + folder; return null; }
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) && shell.Id == "cmd")
        {
            problem = "Command Prompt cannot start in a network path. Use PowerShell or a mapped drive letter.";
            return null;
        }

        var session = new Session(shell, folder);
        session.Start(cols, rows);
        if (session.State == SessionState.Failed) { problem = "Could not start " + shell.MenuName + ": " + session.Problem; return null; }
        _list.Add(session);
        return session;
    }

    /// <summary>Adds a session for a console program that Windows handed over (default terminal).</summary>
    internal Session AddAttached(ShellInfo shell, string folder, Pty pty)
    {
        var session = new Session(shell, folder);
        session.StartAttached(pty);
        _list.Add(session);
        return session;
    }

    public void Remove(Session session)
    {
        session.Close();
        _list.Remove(session);
    }

    public void CloseAll()
    {
        foreach (var s in _list.ToList()) s.Close();
        _list.Clear();
    }
}
