using System.Windows;
using System.Windows.Threading;
using Lumen.Windows.Services;

namespace Lumen.Windows;

public partial class App : Application
{
    public static AppServices? Services { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        Services = new AppServices();
        ThemeManager.Apply(Services.Settings.ThemeMode);
        var window = new MainWindow
        {
            DataContext = Services.MainViewModel
        };
        MainWindow = window;
        window.Show();
        await Services.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Services is not null)
            {
                var dispose = Services.DisposeAsync().AsTask();
                Task.Run(() => dispose).Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch
        {
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "Lumen 遇到错误",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
