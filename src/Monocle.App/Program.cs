using Avalonia;
using Monocle.App.Diagnostics;
using System;
using System.Threading.Tasks;

namespace Monocle.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Logs go to the per-run file (Log.FilePath) and the in-app console drawer (Settings → Show
        // console). No AllocConsole: a WinExe launched from Explorer must not pop a terminal window.
        Log.Init();
        Log.Info($"Monocle starting (args: {string.Join(' ', args)}). Log file: {Log.FilePath}");

        // Catch anything the UI handlers don't, so a stray throw is logged instead of vanishing.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal: the app loop crashed", ex);
            throw;
        }
        finally
        {
            Log.Info("Monocle exiting.");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
