using Lumen.Windows.Models;
using Lumen.Windows.ViewModels;

namespace Lumen.Windows.Services;

public sealed class AppServices : IAsyncDisposable
{
    public AppServices()
    {
        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Settings;
        History = new HistoryStore(SettingsStore);
        Api = new ApiClient(Settings);
        Relay = new RelayConnection(Settings);
        Discovery = new LocalDiscovery(SettingsStore, Settings);
        DirectServer = new DirectTransferServer(SettingsStore, Settings, History);
        DirectClient = new DirectPeerClient();
        Coordinator = new TransferCoordinator(
            Settings,
            SettingsStore,
            History,
            Api,
            Relay,
            DirectClient,
            DirectServer);
        MainViewModel = new MainViewModel(
            SettingsStore,
            Settings,
            History,
            Api,
            Relay,
            Discovery,
            DirectServer,
            Coordinator);
    }

    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public HistoryStore History { get; }
    public ApiClient Api { get; }
    public RelayConnection Relay { get; }
    public LocalDiscovery Discovery { get; }
    public DirectTransferServer DirectServer { get; }
    public DirectPeerClient DirectClient { get; }
    public TransferCoordinator Coordinator { get; }
    public MainViewModel MainViewModel { get; }

    public Task InitializeAsync() => MainViewModel.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            MainViewModel.Dispose();
            await Coordinator.DisposeAsync();
            await Relay.StopAsync();
            Relay.Dispose();
            await Discovery.StopAsync();
            Discovery.Dispose();
            await DirectServer.DisposeAsync();
            DirectClient.Dispose();
            Api.Dispose();
            await History.SaveAsync();
            await SettingsStore.SaveAsync();
            History.Dispose();
            SettingsStore.Dispose();
        }
        catch
        {
        }
    }
}
