using System;
using System.Threading.Tasks;
using Avalonia;
using Googlook.Services;

namespace Googlook;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Last-resort visibility: anything that slips every guard still lands in the log
        // (%AppData%\Googlook\googlook.log) instead of vanishing with the process.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("AppDomain", e.ExceptionObject as Exception
                                   ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("UnobservedTask", e.Exception);
            e.SetObserved();   // a lost background task must not take the process down
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            throw;   // still crash loudly, but with a diagnosable trace on disk
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
            // The email reader and the browser tabs both embed WebView2 (Chromium) via
            // Avalonia's NativeControlHost (Controls/HtmlMessageView.cs, Controls/BrowserView.cs).
            // Browser tabs also offer "Open in Chrome" (Services/ChromeLauncher.cs) as a fallback.
}
