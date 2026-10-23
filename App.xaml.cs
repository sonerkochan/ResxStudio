using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ResxStudio.Services;

namespace ResxStudio;

public partial class App : Application
{
    private readonly Queue<DateTime> _recentErrors = new();
    private bool _showingError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Write($"---- ResxStudio {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}, .NET {Environment.Version}");

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("Fatal error: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("Background task error: " + args.Exception);
            args.SetObserved();
        };
        UiWatchdog.Start(Dispatcher);

        // The window is created here rather than with StartupUri so that if it fails,
        // the app reports it and exits instead of running on without a window.
        try
        {
            Log.Step("Creating the main window");
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            Log.Step("Main window shown");
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't create the main window: " + ex);
            MessageBox.Show($"ResxStudio couldn't start: {ex.Message}\n\nDetails are in {Log.FilePath}",
                "ResxStudio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled error: " + e.Exception);
        e.Handled = true;

        // An error during layout repeats on every frame. Don't stack up message boxes;
        // if it keeps happening, close cleanly instead of leaving a frozen window behind.
        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(10)) _recentErrors.Dequeue();
        if (_recentErrors.Count > 5)
        {
            Log.Write("Too many errors in a row; shutting down.");
            MessageBox.Show($"ResxStudio hit the same error repeatedly and has to close: {e.Exception.Message}\n\nDetails are in {Log.FilePath}",
                "ResxStudio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (_showingError) return;
        _showingError = true;
        try
        {
            MessageBox.Show($"{e.Exception.Message}\n\nDetails are in {Log.FilePath}", "Something went wrong",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }
}
