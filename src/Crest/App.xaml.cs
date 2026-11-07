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
