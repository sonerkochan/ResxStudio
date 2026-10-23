using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace ResxStudio.Services;

/// <summary>
/// Plain-text log in %AppData%\ResxStudio\log.txt. Every startup step is written here, so if the
/// app ever hangs or crashes, the last lines show where.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static volatile string _lastStep = "starting";

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ResxStudio", "log.txt");

    public static string LastStep => _lastStep;

    /// <summary>Records what the app is doing now, for the watchdog's report.</summary>
    public static void Step(string step)
    {
        _lastStep = step;
        Write(step);
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }
}

/// <summary>
/// Background thread that pings the UI thread every few seconds. If the UI stops responding,
/// it writes the last step to the log, which pinpoints a freeze without a debugger.
/// </summary>
public static class UiWatchdog
{
    public static void Start(Dispatcher dispatcher)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                using var answered = new ManualResetEventSlim();
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(answered.Set));
                if (!answered.Wait(TimeSpan.FromSeconds(8)))
                {
                    Log.Write($"FREEZE: the window hasn't responded for 8 seconds. Last step: {Log.LastStep}");
                    answered.Wait();
                    Log.Write("The window is responding again.");
                }
                Thread.Sleep(2000);
            }
        })
        {
            IsBackground = true,
            Name = "UI watchdog",
        };
        thread.Start();
    }
}
