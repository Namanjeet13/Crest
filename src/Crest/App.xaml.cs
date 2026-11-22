using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Crest;

public partial class App : Application
{
    private Mutex? _single;

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crest");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Started by a browser as the Crest Downloads extension's native host: no window, no single-instance check.
        if (Services.BrowserDownloads.IsHostLaunch(e.Args))
        {
            int code = 0;
            try { code = Services.BrowserDownloads.RunHost(); } catch { code = 1; }
            Environment.Exit(code);
            return;
        }

        _single = new Mutex(true, "Crest.SingleInstance.v1", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        Directory.CreateDirectory(DataDir);
        DispatcherUnhandledException += (_, a) =>
        {
            Log(a.Exception);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        base.OnStartup(e);
        // The browser extension isn't listed yet, so Crest doesn't register its native host.
        // Services.BrowserDownloads.Start();
        new NotchWindow().Show();
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "crash.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
