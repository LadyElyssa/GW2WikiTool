using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GW2WikiTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Without these, an exception during window construction/XAML load can terminate the
        // process before any UI ever appears -- which looks exactly like "nothing happened".
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Unhandled UI exception:\n\n{args.Exception}",
                "GW2WikiTool - Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Unhandled exception:\n\n{args.ExceptionObject}",
                "GW2WikiTool - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            MessageBox.Show(
                $"Unobserved background task exception:\n\n{args.Exception}",
                "GW2WikiTool - Background Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.SetObserved();
        };

        base.OnStartup(e);
    }
}
