using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Lumen.Windows.Services;
using Lumen.Windows.ViewModels;

namespace Lumen.Windows;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\Lumen.Windows.SingleInstance";
    private const string ActivateEventName = @"Local\Lumen.Windows.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    public static AppServices? Services { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                    break;
                }

                Thread.Sleep(150);
            }

            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        Services = new AppServices();
        ThemeManager.Apply(Services.Settings.ThemeMode);
        var window = new MainWindow
        {
            DataContext = Services.MainViewModel
        };
        MainWindow = window;
        window.Show();

        var viewModel = Services.MainViewModel;
        Tray = new TrayIcon(
            window,
            () => $"{viewModel.LanStatus} · {viewModel.ServerStatus}",
            () =>
            {
                window.AllowClose();
                window.Close();
                Shutdown();
            });
        window.HideToTrayRequested += (_, _) =>
        {
            window.Hide();
            Tray.Notify("Lumen 仍在后台运行", "关闭窗口不会退出，托盘图标可右键退出或双击打开。");
        };
        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    if (!_activateEvent.WaitOne())
                    {
                        return;
                    }

                    Dispatcher.Invoke(() => Tray?.Restore());
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.LanStatus) or nameof(MainViewModel.ServerStatus))
            {
                Tray?.UpdateTooltip($"Lumen · {viewModel.LanStatus} · {viewModel.ServerStatus}");
            }
        };

        await Services.InitializeAsync();

        var smokeTheme = Environment.GetEnvironmentVariable("LUMEN_SMOKE_TOGGLE_THEME");
        if (!string.IsNullOrWhiteSpace(smokeTheme))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ThemeManager.Apply(smokeTheme);
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        Tray = null;
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
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

    public static TrayIcon? Tray { get; private set; }

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
