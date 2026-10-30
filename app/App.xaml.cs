using CmdWorkspace.Core;
using Microsoft.UI.Xaml;

namespace CmdWorkspace;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // Phase 12b: the uninstaller calls this to put the Windows console settings back. No window.
        if (Environment.GetCommandLineArgs().Any(a => a.Equals(DefaultTerminal.RestoreArg, StringComparison.OrdinalIgnoreCase)))
        {
            DefaultTerminal.Disable();
            Environment.Exit(0);
        }
        // Phase 11b: a second normal start gives its folder to the running window and ends here.
        if (SingleInstance.TryHandOver(StartFolder.Requested())) Environment.Exit(0);
        AppState.Load();
        ProjectStore.Load();
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        // Phase 7: errors on background threads (shell output, WSL list) are logged too.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("background thread: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("ERROR unobserved task exception: " + e.Exception); e.SetObserved(); };
        Log.Write("App started. Logs folder: " + Log.Dir);
        WebEnv.Init();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    // Crash text goes to logs\crash.log (only the error, never terminal text or clipboard text).
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception.ToString());
    }

    private static void LogCrash(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(Log.Dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never cause a second crash.
        }
    }
}
