using System.Windows;
using System.Windows.Threading;

namespace ResxStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    // Show the problem instead of crashing, so unsaved work isn't lost.
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
