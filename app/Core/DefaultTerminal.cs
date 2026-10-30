using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CmdWorkspace.Core;

/// <summary>
/// Default terminal (Phase 12b). OFF unless the user turns it on in Settings > Shell.
/// When on, Windows hands a new console program (cmd, PowerShell, a program started from Win+R or Explorer) to CMD Workspace
/// instead of opening its own console window. The interface (ITerminalHandoff3) is undocumented and may change in Windows.
/// What this file changes in the registry, only for the current user, and only when the user turns the switch on:
///   HKCU\Console\%%Startup  DelegationConsole and DelegationTerminal
///   HKCU\Software\Classes\CLSID\{our id}\LocalServer32
/// The old values are saved first (default-terminal-backup.json) and put back when the switch is turned off or the app is uninstalled.
/// </summary>
public static class DefaultTerminal
{
    public const string Clsid = "{3B7D619E-8A2F-4954-A488-FDD3E58776D1}";
    public const string RestoreArg = "--restore-default-terminal";
    // The spike of Phase 11b/12a used this id. If it is still registered, the user must remove it first.
    private const string SpikeClsid = "{7A3C1E5B-2D4F-4B8A-9C6E-5F1D0A2B3C4D}";
    // Windows' own console side of the pair (the same one Windows Terminal uses). Same value as in the 12a test, which worked.
    private const string ConsoleClsid = "{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}";
    private const string StartupPath = @"Console\%%Startup";
    private static readonly string BackupPath = Path.Combine(AppState.Folder, "default-terminal-backup.json");

    internal sealed class Backup
    {
        public bool KeyExisted { get; set; }
        public string? DelegationConsole { get; set; }
        public string? DelegationTerminal { get; set; }
    }

    /// <summary>True when the two Windows values point to this app.</summary>
    public static bool IsOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupPath);
            return key != null && Same(key.GetValue("DelegationTerminal") as string, Clsid);
        }
        catch { return false; }
    }

    private static bool Same(string? a, string b) => a != null && a.Trim().Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns it on. Returns null when done, else a plain sentence (nothing is left half changed).</summary>
    public static string? Enable()
    {
        if (Elevation.IsElevated) return "This is not available in an administrator window.";
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return "The program file was not found.";
        if (IsOn()) return null;
        var wroteBackup = false;
        try
        {
            using (var peek = Registry.CurrentUser.OpenSubKey(StartupPath))
            {
                var term = peek?.GetValue("DelegationTerminal") as string;
                if (Same(term, SpikeClsid)) return "The Phase 11/12 test is still registered. Run extras\\defterm-test-unregister.cmd first.";
            }
            if (File.Exists(BackupPath)) return "An old backup file exists: " + BackupPath + ". Turn the switch off once, or send this file to Claude. Nothing was changed.";

            var backup = new Backup();
            using (var key = Registry.CurrentUser.OpenSubKey(StartupPath))
            {
                backup.KeyExisted = key != null;
                backup.DelegationConsole = key?.GetValue("DelegationConsole") as string;
                backup.DelegationTerminal = key?.GetValue("DelegationTerminal") as string;
            }
            Directory.CreateDirectory(AppState.Folder);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            wroteBackup = true;
            Log.Write($"default terminal: backup saved (console {backup.DelegationConsole ?? "none"}, terminal {backup.DelegationTerminal ?? "none"})");

            WriteServerKey(exe);
            using (var key = Registry.CurrentUser.CreateSubKey(StartupPath))
            {
                key.SetValue("DelegationConsole", ConsoleClsid, RegistryValueKind.String);
                key.SetValue("DelegationTerminal", Clsid, RegistryValueKind.String);
            }
            Log.Write("default terminal: ON");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("default terminal: enable", ex);
            var problem = ex.Message;
            // Put back what was there. If that fails too, say so.
            var back = wroteBackup ? Disable() : null;
            return "Could not turn it on: " + problem + (back != null ? " Putting the old values back failed too: " + back : " Nothing was changed.");
        }
    }

    private static void WriteServerKey(string exe)
    {
        using var cls = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + Clsid);
        cls.SetValue("", "CMD Workspace default terminal", RegistryValueKind.String);
        using var server = cls.CreateSubKey("LocalServer32");
        server.SetValue("", "\"" + exe + "\" -Embedding", RegistryValueKind.String);
    }

    /// <summary>If it is on but the program moved (reinstall in another folder), the path is corrected. Returns true when it is on.</summary>
    public static bool RefreshPath()
    {
        try
        {
            if (!IsOn()) return false;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return true;
            using var server = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\" + Clsid + @"\LocalServer32");
            var wanted = "\"" + exe + "\" -Embedding";
            if (!string.Equals(server?.GetValue("") as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                WriteServerKey(exe);
                Log.Write("default terminal: program path updated");
            }
            return true;
        }
        catch (Exception ex) { Log.Error("default terminal: refresh", ex); return IsOn(); }
    }

    /// <summary>Turns it off and puts the old values back. Returns null when done, else a plain sentence.</summary>
    public static string? Disable()
    {
        try
        {
            Backup? backup = null;
            string? note = null;
            if (File.Exists(BackupPath))
            {
                try { backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)); }
                catch (Exception ex) { note = "The backup file could not be read (" + ex.Message + "), so the two values were removed and Windows uses its own default."; }
            }
            using (var key = Registry.CurrentUser.OpenSubKey(StartupPath, true))
            {
                // Only when the values point to this app are they touched. Values somebody else set since stay as they are.
                if (key != null && Same(key.GetValue("DelegationTerminal") as string, Clsid))
                {
                    SetOrDelete(key, "DelegationConsole", backup?.DelegationConsole);
                    SetOrDelete(key, "DelegationTerminal", backup?.DelegationTerminal);
                }
            }
            if (backup is { KeyExisted: false })
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupPath);
                if (key != null && key.ValueCount == 0 && key.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(StartupPath, false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + Clsid, false);
            if (File.Exists(BackupPath)) File.Delete(BackupPath);
            Log.Write("default terminal: OFF, old values restored" + (note != null ? " (" + note + ")" : ""));
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("default terminal: disable", ex);
            return ex.Message;
        }
    }

    private static void SetOrDelete(RegistryKey key, string name, string? old)
    {
        if (old == null) key.DeleteValue(name, false);
        else key.SetValue(name, old, RegistryValueKind.String);
    }

    // ------------------------------------------------------------------ the COM server (inside the running app)

    /// <summary>What Windows hands over for one console program.</summary>
    public sealed class HandoffInfo
    {
        public IntPtr FromConsole;   // we read the screen text here
        public IntPtr ToConsole;     // we write the keystrokes here
        public IntPtr Client;        // the console program (process handle)
        public IntPtr Signal;        // resize messages go here
        public IntPtr Reference;
        public IntPtr Server;
        public string ClientExe = "";
        public int ClientId;

        public void CloseAll()
        {
            foreach (var h in new[] { FromConsole, ToConsole, Client, Signal, Reference, Server })
                if (h != IntPtr.Zero) Native.CloseHandle(h);
        }
    }

    /// <summary>Set by the window. Returns true when a session took the hand over. It is called on a background thread.</summary>
    public static Func<HandoffInfo, bool>? Accept;

    private static Thread? _server;
    private static readonly ManualResetEvent Stop = new(false);
    private static int _running;

    /// <summary>Starts answering Windows (only while the switch is on, only in a normal window).</summary>
    public static void StartServer()
    {
        if (Elevation.IsElevated || !IsOn()) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        Stop.Reset();
        _server = new Thread(ServerLoop) { IsBackground = true, Name = "default terminal server" };
        _server.SetApartmentState(ApartmentState.MTA);
        _server.Start();
    }

    public static void StopServer()
    {
        if (Interlocked.CompareExchange(ref _running, 0, 1) == 0) return;
        Stop.Set();
    }

    private static void ServerLoop()
    {
        uint cookie = 0;
        var registered = false;
        try
        {
            Native.CoInitializeEx(IntPtr.Zero, 0);   // multi threaded
            var factory = new Factory();
            var pf = Marshal.GetComInterfaceForObject(factory, typeof(IClassFactory));
            var id = new Guid(Clsid);
            var hr = Native.CoRegisterClassObject(ref id, pf, 4 /* CLSCTX_LOCAL_SERVER */, 1 /* REGCLS_MULTIPLEUSE */, out cookie);
            if (hr != 0) { Log.Write("default terminal: CoRegisterClassObject failed 0x" + hr.ToString("X8")); return; }
            registered = true;
            Log.Write("default terminal: listening");
            Stop.WaitOne();
        }
        catch (Exception ex) { Log.Error("default terminal server", ex); }
        finally
        {
            try { if (registered) Native.CoRevokeClassObject(cookie); } catch { }
            Interlocked.Exchange(ref _running, 0);
            Log.Write("default terminal: stopped listening");
        }
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    // Same signature as in the 12a test, which Windows called (verified in defterm-spike.log).
    [ComImport, Guid("6F23DA90-15C5-4203-9DB0-64E73F1B1B00"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITerminalHandoff3
    {
        [PreserveSig]
        int EstablishPtyHandoff(out IntPtr input, out IntPtr output, IntPtr signal, IntPtr reference, IntPtr server, IntPtr client, IntPtr startupInfo);
    }

    private const int EFail = unchecked((int)0x80004005);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Handoff : ITerminalHandoff3
    {
        public int EstablishPtyHandoff(out IntPtr input, out IntPtr output, IntPtr signal, IntPtr reference, IntPtr server, IntPtr client, IntPtr startupInfo)
        {
            input = IntPtr.Zero;
            output = IntPtr.Zero;
            HandoffInfo? info = null;
            IntPtr outWrite = IntPtr.Zero, inRead = IntPtr.Zero;
            try
            {
                var accept = Accept;
                if (accept == null || !IsOn()) { Log.Write("default terminal: request refused (window not ready or switch off), Windows opens its own console"); return EFail; }

                // The terminal makes the two pipes (version 3 of the interface) and gives the console side ends back to Windows.
                if (!Native.CreatePipe(out var outRead, out outWrite, IntPtr.Zero, 0)) throw new InvalidOperationException("output pipe " + Marshal.GetLastWin32Error());
                if (!Native.CreatePipe(out inRead, out var inWrite, IntPtr.Zero, 0))
                {
                    Native.CloseHandle(outRead); Native.CloseHandle(outWrite);
                    throw new InvalidOperationException("input pipe " + Marshal.GetLastWin32Error());
                }
                info = new HandoffInfo
                {
                    FromConsole = outRead, ToConsole = inWrite,
                    Client = Keep(client), Signal = Keep(signal), Reference = Keep(reference), Server = Keep(server)
                };
                if (info.Client != IntPtr.Zero)
                {
                    info.ClientId = Native.GetProcessId(info.Client);
                    var sb = new StringBuilder(1024);
                    var size = sb.Capacity;
                    if (Native.QueryFullProcessImageNameW(info.Client, 0, sb, ref size)) info.ClientExe = sb.ToString();
                }
                Log.Write($"default terminal: request from process {info.ClientId} ({(info.ClientExe.Length > 0 ? Path.GetFileName(info.ClientExe) : "unknown program")})");

                if (!accept(info)) { Log.Write("default terminal: window did not take it, Windows opens its own console"); info.CloseAll(); Native.CloseHandle(outWrite); Native.CloseHandle(inRead); return EFail; }

                input = inRead;
                output = outWrite;
                // Windows has its own copies after this call returns. Ours are closed a little later, so the pipe ends when the console program ends.
                var late1 = outWrite; var late2 = inRead;
                _ = Task.Run(async () => { await Task.Delay(5000); Native.CloseHandle(late1); Native.CloseHandle(late2); });
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("default terminal: handoff", ex);
                try { info?.CloseAll(); } catch { }
                if (outWrite != IntPtr.Zero) Native.CloseHandle(outWrite);
                if (inRead != IntPtr.Zero) Native.CloseHandle(inRead);
                input = IntPtr.Zero;
                output = IntPtr.Zero;
                return EFail;
            }
        }

        private static IntPtr Keep(IntPtr h)
        {
            if (h == IntPtr.Zero) return IntPtr.Zero;
            var me = Native.GetCurrentProcess();
            return Native.DuplicateHandle(me, h, me, out var d, 0, false, 2 /* DUPLICATE_SAME_ACCESS */) ? d : IntPtr.Zero;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Factory : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj)
        {
            obj = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110);   // CLASS_E_NOAGGREGATION
            var unk = Marshal.GetIUnknownForObject(new Handoff());
            try { return Marshal.QueryInterface(unk, ref riid, out obj); }
            finally { Marshal.Release(unk); }
        }

        public int LockServer(bool fLock) => 0;
    }

    internal static class Native
    {
        [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr reserved, uint options);
        [DllImport("ole32.dll")] public static extern int CoRegisterClassObject(ref Guid clsid, IntPtr unk, uint context, uint flags, out uint cookie);
        [DllImport("ole32.dll")] public static extern int CoRevokeClassObject(uint cookie);
        [DllImport("kernel32.dll")] public static extern int GetProcessId(IntPtr process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    }
}
