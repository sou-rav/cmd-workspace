using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CmdWorkspace.Core;
using Microsoft.Win32.SafeHandles;

namespace CmdWorkspace.Term;

/// <summary>
/// One Windows pseudo console with one shell program in it.
/// The start folder goes to CreateProcess. Nothing is ever typed into the shell.
/// The shell sits in a job object, so Windows ends it if this app dies.
/// </summary>
internal sealed class Pty : IDisposable
{
    private IntPtr _console;
    private IntPtr _process, _thread, _job;
    private SafeFileHandle? _toShell, _fromShell;
    private FileStream? _writer;
    private int _disposed;
    // Default terminal (Phase 12b): Windows made the console program and gave us the pipes. No pseudo console of our own.
    private bool _attached;
    private IntPtr _reference, _server;
    private FileStream? _signal;
    private int _exitRaised;

    public int ProcessId { get; private set; }

    /// <summary>Text from the shell. Raised on a background thread.</summary>
    public event Action<string>? Output;

    /// <summary>The shell ended, with its exit code. Raised on a background thread.</summary>
    public event Action<int>? Exited;

    public static Pty Start(string exe, string args, string folder, short cols, short rows)
    {
        var pty = new Pty();
        try { pty.Launch(exe, args, folder, cols, rows); }
        catch { pty.Dispose(); throw; }   // Phase 7: a failed start no longer leaves a pseudo console open
        return pty;
    }

    /// <summary>Takes over a console program that Windows started (default terminal). The handles belong to this object from now on.</summary>
    public static Pty Attach(IntPtr fromConsole, IntPtr toConsole, IntPtr client, IntPtr signal, IntPtr reference, IntPtr server)
    {
        var pty = new Pty { _attached = true };
        pty._fromShell = new SafeFileHandle(fromConsole, true);
        pty._toShell = new SafeFileHandle(toConsole, true);
        pty._writer = new FileStream(pty._toShell, FileAccess.Write, 1, false);
        pty._process = client;
        pty._reference = reference;
        pty._server = server;
        if (signal != IntPtr.Zero) pty._signal = new FileStream(new SafeFileHandle(signal, true), FileAccess.Write, 1, false);
        pty.ProcessId = client != IntPtr.Zero ? GetProcessId(client) : 0;
        return pty;
    }

    private void Launch(string exe, string args, string folder, short cols, short rows)
    {
        if (!CreatePipe(out var shellIn, out var toShell, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var fromShell, out var shellOut, IntPtr.Zero, 0)) throw new Win32Exception();
        _toShell = toShell;
        _fromShell = fromShell;

        var hr = CreatePseudoConsole(new Coord { X = cols, Y = rows }, shellIn, shellOut, 0, out _console);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");

        // The attribute list ties the new process to the pseudo console.
        var listSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        var list = Marshal.AllocHGlobal(listSize);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref listSize)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020016, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new StartupInfoEx();
            startup.Info.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.AttributeList = list;
            // The shell must use the pseudo console, never this program's own standard handles.
            // Without this, a redirected output (for example "dotnet run > file") makes the shell write there instead of to the card.
            // Found on the user's PC on 2026-10-24 (output ended in logs\run.log). Same method as other terminal programs: flag set, all three handles empty.
            startup.Info.Flags = 0x00000100; // STARTF_USESTDHANDLES
            startup.Info.StdIn = IntPtr.Zero;
            startup.Info.StdOut = IntPtr.Zero;
            startup.Info.StdErr = IntPtr.Zero;

            var commandLine = new StringBuilder("\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args));
            const uint extendedStartupInfo = 0x00080000, unicodeEnvironment = 0x00000400;
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, extendedStartupInfo | unicodeEnvironment,
                    IntPtr.Zero, string.IsNullOrEmpty(folder) ? null : folder, ref startup, out var info))
                throw new Win32Exception();

            _process = info.Process;
            _thread = info.Thread;
            ProcessId = info.ProcessId;
        }
        finally
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }

        // The pseudo console keeps its own copy of these two pipe ends.
        shellIn.Dispose();
        shellOut.Dispose();

        PutInJob();
        _writer = new FileStream(_toShell, FileAccess.Write, 1, false);
    }

    /// <summary>Starts reading output and watching for the end. Call after the handlers are attached, so nothing is missed.</summary>
    public void BeginRead()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "pty read" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty wait" }.Start();
    }

    private void PutInJob()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        var limits = new JobExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // end all processes when the job closes
        var size = Marshal.SizeOf(limits);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            SetInformationJobObject(_job, 9, buffer, (uint)size);
            AssignProcessToJobObject(_job, _process);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16384];
        var chars = new char[16384];
        try
        {
            using var stream = new FileStream(_fromShell!, FileAccess.Read, 1, false);
            while (true)
            {
                var n = stream.Read(bytes, 0, bytes.Length);
                if (n <= 0) break;
                var count = decoder.GetChars(bytes, 0, n, chars, 0, false);
                if (count > 0) Output?.Invoke(new string(chars, 0, count));
            }
        }
        catch { /* the pipe was closed */ }
        // Attached: the pipe ending means the console is gone. This also covers a process handle that cannot be waited on.
        if (_attached) RaiseExited();
    }

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) == 1) return;
        uint code = 0;
        try
        {
            if (_process != IntPtr.Zero && GetExitCodeProcess(_process, out var c) && c != 259 /* still active */) code = c;
        }
        catch { }
        Exited?.Invoke((int)code);
    }

    private void WaitLoop()
    {
        var result = WaitForSingleObject(_process, 0xFFFFFFFF);
        if (_attached && result != 0)
        {
            Log.Write("default terminal: the console program cannot be waited on (result " + result + "), the end is noticed when its pipe closes");
            return;
        }
        RaiseExited();
    }

    /// <summary>True when the shell has a child program running (conhost is ignored).</summary>
    public bool HasChildren()
    {
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == (IntPtr)(-1)) return false;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return false;
            do
            {
                if (entry.ParentId == (uint)ProcessId && entry.Id != (uint)ProcessId
                    && !entry.Exe.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
                    && !entry.Exe.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase))
                    return true;
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return false;
    }

    public void Write(string text)
    {
        var stream = _writer;
        if (stream == null) return;
        var data = Encoding.UTF8.GetBytes(text);
        lock (stream)
        {
            try { stream.Write(data, 0, data.Length); stream.Flush(); } catch { /* shell already ended */ }
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_console != IntPtr.Zero) ResizePseudoConsole(_console, new Coord { X = cols, Y = rows });
        else if (_attached && _signal != null)
        {
            // Best guess of the console host's signal format (not verified): message 8 (resize), then columns and rows, each 16 bit.
            var packet = new byte[] { 8, 0, (byte)(cols & 0xFF), (byte)(cols >> 8), (byte)(rows & 0xFF), (byte)(rows >> 8) };
            lock (_signal)
            {
                try { _signal.Write(packet, 0, packet.Length); _signal.Flush(); } catch { /* console already ended */ }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { if (_process != IntPtr.Zero) TerminateProcess(_process, 1); } catch { }
        var console = _console;
        _console = IntPtr.Zero;
        // ClosePseudoConsole can wait for the reader, so it runs on its own thread and never blocks the window.
        Task.Run(() =>
        {
            try { if (console != IntPtr.Zero) ClosePseudoConsole(console); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _fromShell?.Dispose(); } catch { }
            try { _signal?.Dispose(); } catch { }
            if (_reference != IntPtr.Zero) CloseHandle(_reference);
            if (_server != IntPtr.Zero) CloseHandle(_server);
            if (_thread != IntPtr.Zero) CloseHandle(_thread);
            if (_process != IntPtr.Zero) CloseHandle(_process);
            if (_job != IntPtr.Zero) CloseHandle(_job);
        });
    }

    // ---------------------------------------------------------------- Windows API

    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public IntPtr Reserved3, StdIn, StdOut, StdErr;
    }

    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr AttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTime, PerJobUserTime; public uint LimitFlags;
        public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id; public UIntPtr DefaultHeap; public uint ModuleId, Threads, ParentId;
        public int PriorityBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder commandLine, IntPtr processAttrs, IntPtr threadAttrs, bool inherit,
        uint flags, IntPtr environment, string? folder, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern int GetProcessId(IntPtr process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr handle, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
