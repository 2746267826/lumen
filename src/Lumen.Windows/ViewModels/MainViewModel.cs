using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Shared;
using Lumen.Windows.Infrastructure;
using Lumen.Windows.Models;
using Lumen.Windows.Services;
using Microsoft.Win32;

namespace Lumen.Windows.ViewModels;

public sealed record ChunkSizeOption(int Bytes, string Label);
public sealed record ThemeOption(string Key, string Label);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly ApiClient _api;
    private readonly RelayConnection _relay;
    private readonly LocalDiscovery _discovery;
    private readonly DirectTransferServer _directServer;
    private readonly TransferCoordinator _coordinator;
    private readonly Dictionary<Guid, PeerDevice> _deviceMap = new();
    private readonly HashSet<Guid> _serverDeviceIds = [];
    private readonly Queue<LocalTransferRecord> _inviteQueue = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly DispatcherTimer _toastTimer;
    private PeerDevice? _composePeer;
    private LocalTransferRecord? _pendingInvite;
    private LocalTransferRecord? _selectedTransfer;
    private ShareItem? _createdShare;
    private string? _shareFilePath;
    private string _shareText = "";
    private string _shareKind = TransferKinds.File;
    private string _shareName = "";
    private bool _isInitialized;
    private bool _isAuthenticating;
    private bool _isSending;
    private bool _isDetailOpen;
    private bool _isComposeOpen;
    private bool _isShareDialogOpen;
    private bool _isCreatingShare;
    private bool _toastVisible;
    private bool _toastIsError;
    private string _toastMessage = "";
    private string _currentPage = "devices";
    private string _transferFilter = "all";
    private string _serverUrl;
    private string _deviceName;
    private string _receiveDirectory;
    private string _usernameInput;
    private string _displayNameInput;
    private string _passwordInput = "";
    private string _composeText = "";
    private string _composeTargetLabel = "";
    private string _shareDialogTitle = "创建分享";
    private string _shareTargetLabel = "";
    private string _sharePassword = "";
    private string _shareExpiryHours = "24";
    private string _shareMaxDownloads = "0";
    private string _serverStatus = "未连接";
    private string _serverStatusColor = "#7B849E";
    private string _lanStatus = "局域网未启动";
    private string _lanStatusColor = "#7B849E";
    private string _localIpv4 = "—";
    private string _localIpv6 = "—";
    private string _directStatus = "—";
    private string _discoveryStatus = "—";
    private ChunkSizeOption _selectedChunkSize;
    private ThemeOption _selectedTheme;

    public MainViewModel(
        SettingsStore settingsStore,
        AppSettings settings,
        HistoryStore history,
        ApiClient api,
        RelayConnection relay,
        LocalDiscovery discovery,
        DirectTransferServer directServer,
        TransferCoordinator coordinator)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _history = history;
        _api = api;
        _relay = relay;
        _discovery = discovery;
        _directServer = directServer;
        _coordinator = coordinator;
        _serverUrl = settings.ServerUrl;
        _deviceName = settings.DeviceName;
        _receiveDirectory = settings.ReceiveDirectory;
        _usernameInput = settings.Username ?? "";
        _displayNameInput = settings.DisplayName ?? "";

        ChunkOptions =
        [
            new(256 * 1024, "256 KiB"),
            new(512 * 1024, "512 KiB"),
            new(1024 * 1024, "1 MiB（推荐）"),
            new(2 * 1024 * 1024, "2 MiB"),
            new(4 * 1024 * 1024, "4 MiB")
        ];
        _selectedChunkSize = ChunkOptions.FirstOrDefault(x => x.Bytes == settings.ChunkSize)
                             ?? ChunkOptions[2];
        ThemeOptions =
        [
            new(ThemeManager.SystemMode, "跟随系统"),
            new(ThemeManager.LightMode, "浅色"),
            new(ThemeManager.DarkMode, "深色")
        ];
        _selectedTheme = ThemeOptions.FirstOrDefault(x => x.Key == settings.ThemeMode)
                         ?? ThemeOptions[0];

        Devices = new ObservableCollection<PeerDevice>();
        Shares = new ObservableCollection<ShareItem>();
        LocalAddresses = new ObservableCollection<LocalAddressInfo>();
        TransfersView = CollectionViewSource.GetDefaultView(history.Records);
        TransfersView.Filter = FilterTransfer;
        history.Records.CollectionChanged += (_, _) =>
        {
            TransfersView.Refresh();
            RaiseTransferSummary();
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastVisible = false;
        };

        NavigateCommand = new RelayCommand(parameter =>
            Navigate(parameter as string ?? "devices"));
        SetFilterCommand = new RelayCommand(parameter =>
            TransferFilter = parameter as string ?? "all");
        ConnectCommand = Async(ConnectAsync, () => !IsAuthenticating);
        RegisterCommand = Async(RegisterAsync, () => !IsAuthenticating);
        LogoutCommand = Async(LogoutAsync);
        RefreshCommand = Async(RefreshAllAsync);
        SaveSettingsCommand = Async(SaveSettingsAsync);
        ChooseReceiveFolderCommand = new RelayCommand(_ => ChooseReceiveFolder());
        SendFilesCommand = Async(
            parameter => SendFilesAsync(parameter as PeerDevice),
            parameter => parameter is PeerDevice { CanSend: true } && !IsSending);
        SendImageCommand = Async(
            parameter => SendImagesAsync(parameter as PeerDevice),
            parameter => parameter is PeerDevice { CanSend: true } && !IsSending);
        SendTextCommand = new RelayCommand(OpenCompose);
        SendClipboardCommand = Async(
            parameter => SendClipboardAsync(parameter as PeerDevice),
            parameter => parameter is PeerDevice { CanSend: true } && !IsSending);
        ConfirmComposeCommand = Async(SendComposeAsync, () => !string.IsNullOrWhiteSpace(ComposeText));
        CloseComposeCommand = new RelayCommand(_ => IsComposeOpen = false);
        PasteClipboardCommand = new RelayCommand(_ => PasteClipboard());
        OpenTransferCommand = new RelayCommand(parameter =>
        {
            if (parameter is LocalTransferRecord record)
            {
                SelectedTransfer = record;
                IsDetailOpen = true;
            }
        });
        CloseDetailCommand = new RelayCommand(_ => IsDetailOpen = false);
        CancelTransferCommand = Async(CancelTransferAsync);
        OpenFileCommand = new RelayCommand(parameter => OpenFile(parameter as LocalTransferRecord));
        OpenFolderCommand = new RelayCommand(parameter => OpenFolder(parameter as LocalTransferRecord));
        CopyTextCommand = new RelayCommand(parameter => CopyText(parameter as string));
        AcceptInviteCommand = Async(AcceptInviteAsync);
        DeclineInviteCommand = Async(DeclineInviteAsync);
        CreateShareFileCommand = new RelayCommand(_ => ChooseShareFile());
        CreateTextShareCommand = new RelayCommand(_ => OpenTextShare());
        ShareTransferCommand = new RelayCommand(parameter => ShareTransfer(parameter as LocalTransferRecord));
        ConfirmShareCommand = Async(CreateShareAsync, () => !IsCreatingShare);
        CloseShareDialogCommand = new RelayCommand(_ => CloseShareDialog());
        CopyCreatedShareCommand = new RelayCommand(_ => CopyText(CreatedShare?.Url));
        CopyShareUrlCommand = new RelayCommand(parameter => CopyText((parameter as ShareItem)?.Url));
        OpenShareUrlCommand = new RelayCommand(parameter => OpenUrl((parameter as ShareItem)?.Url));
        RevokeShareCommand = Async(parameter => RevokeShareAsync(parameter as ShareItem));
        RefreshSharesCommand = Async(RefreshSharesAsync);
        CopyDeviceIdCommand = new RelayCommand(_ => CopyText(_settings.DeviceId.ToString()));
        OpenDataFolderCommand = new RelayCommand(_ => OpenPath(_settingsStore.DataDirectory));

        _relay.ConnectionChanged += RelayOnConnectionChanged;
        _relay.LatencyChanged += (_, latency) => UiThread.Post(() =>
        {
            LatencyMs = latency;
            UpdateConnectionText();
        });
        _relay.MessageReceived += (_, message) => _ = HandleRelayMessageAsync(message);
        _discovery.PeerSeen += (_, packet) => UiThread.Post(() => MergeLanPeer(packet));
        _discovery.PeerLost += (_, id) => UiThread.Post(() => MarkLanPeerLost(id));
        _discovery.StateChanged += (_, _) => UiThread.Post(UpdateDiscoveryState);
        _coordinator.IncomingInvite += (_, record) => UiThread.Post(() => EnqueueInvite(record));
        _coordinator.Notification += (_, notification) =>
            UiThread.Post(() => ShowToast(notification.Message, notification.IsError));
        _coordinator.ActivityChanged += (_, _) =>
        {
            IsSending = _coordinator.IsSending;
            CommandManager.InvalidateRequerySuggested();
        };
    }

    public ObservableCollection<PeerDevice> Devices { get; }
    public ObservableCollection<LocalTransferRecord> Transfers => _history.Records;
    public ObservableCollection<ShareItem> Shares { get; }
    public ObservableCollection<LocalAddressInfo> LocalAddresses { get; }
    public ICollectionView TransfersView { get; }
    public IReadOnlyList<ChunkSizeOption> ChunkOptions { get; }

    public ICommand NavigateCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand RegisterCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ChooseReceiveFolderCommand { get; }
    public ICommand SendFilesCommand { get; }
    public ICommand SendImageCommand { get; }
    public ICommand SendTextCommand { get; }
    public ICommand SendClipboardCommand { get; }
    public ICommand ConfirmComposeCommand { get; }
    public ICommand CloseComposeCommand { get; }
    public ICommand PasteClipboardCommand { get; }
    public ICommand OpenTransferCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand CancelTransferCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyTextCommand { get; }
    public ICommand AcceptInviteCommand { get; }
    public ICommand DeclineInviteCommand { get; }
    public ICommand CreateShareFileCommand { get; }
    public ICommand CreateTextShareCommand { get; }
    public ICommand ShareTransferCommand { get; }
    public ICommand ConfirmShareCommand { get; }
    public ICommand CloseShareDialogCommand { get; }
    public ICommand CopyCreatedShareCommand { get; }
    public ICommand CopyShareUrlCommand { get; }
    public ICommand OpenShareUrlCommand { get; }
    public ICommand RevokeShareCommand { get; }
    public ICommand RefreshSharesCommand { get; }
    public ICommand CopyDeviceIdCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public string ReceiveDirectory
    {
        get => _receiveDirectory;
        set => SetProperty(ref _receiveDirectory, value);
    }

    public string UsernameInput
    {
        get => _usernameInput;
        set => SetProperty(ref _usernameInput, value);
    }

    public string DisplayNameInput
    {
        get => _displayNameInput;
        set => SetProperty(ref _displayNameInput, value);
    }

    public string PasswordInput
    {
        get => _passwordInput;
        set => SetProperty(ref _passwordInput, value);
    }

    public ChunkSizeOption SelectedChunkSize
    {
        get => _selectedChunkSize;
        set
        {
            if (SetProperty(ref _selectedChunkSize, value))
            {
                OnPropertyChanged(nameof(ChunkDetailLabel));
            }
        }
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null || !SetProperty(ref _selectedTheme, value))
            {
                return;
            }

            _settings.ThemeMode = value.Key;
            ThemeManager.Apply(value.Key);
            _ = PersistThemeAsync();
        }
    }

    public string CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value))
            {
                return;
            }

            OnPropertiesChanged(
                nameof(IsDevicesPage),
                nameof(IsTransfersPage),
                nameof(IsSharesPage),
                nameof(IsSettingsPage),
                nameof(PageTitle),
                nameof(PageSubtitle));
        }
    }

    public bool IsDevicesPage => CurrentPage == "devices";
    public bool IsTransfersPage => CurrentPage == "transfers";
    public bool IsSharesPage => CurrentPage == "shares";
    public bool IsSettingsPage => CurrentPage == "settings";

    public string PageTitle => CurrentPage switch
    {
        "transfers" => "传输记录",
        "shares" => "分享链接",
        "settings" => "设备与连接",
        _ => "附近设备"
    };

    public string PageSubtitle => CurrentPage switch
    {
        "transfers" => "每一次传输的路线、速度、分块和状态",
        "shares" => "生成带有效期与密码保护的公开链接",
        "settings" => "服务器绑定、网络状态和本地偏好",
        _ => "已绑定设备自动上线，同时自动发现同一局域网内的设备"
    };

    public string TransferFilter
    {
        get => _transferFilter;
        set
        {
            if (!SetProperty(ref _transferFilter, value))
            {
                return;
            }

            TransfersView.Refresh();
            OnPropertiesChanged(
                nameof(IsAllFilter),
                nameof(IsSentFilter),
                nameof(IsReceivedFilter),
                nameof(IsLanFilter),
                nameof(IsRelayFilter));
        }
    }

    public bool IsAllFilter => TransferFilter == "all";
    public bool IsSentFilter => TransferFilter == "out";
    public bool IsReceivedFilter => TransferFilter == "in";
    public bool IsLanFilter => TransferFilter == "lan";
    public bool IsRelayFilter => TransferFilter == "relay";

    public bool IsAuthenticating
    {
        get => _isAuthenticating;
        private set
        {
            if (SetProperty(ref _isAuthenticating, value))
            {
                OnPropertyChanged(nameof(IsNotAuthenticating));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNotAuthenticating => !IsAuthenticating;

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                OnPropertiesChanged(nameof(SendButtonLabel), nameof(TransferActivityLabel));
            }
        }
    }

    public string SendButtonLabel => IsSending ? "发送中…" : "发送文件";
    public string TransferActivityLabel => IsSending ? "正在处理传输任务" : "传输通道就绪";

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(_settings.AuthToken);
    public string AccountLabel => IsLoggedIn
        ? _settings.DisplayName ?? _settings.Username ?? "已绑定账户"
        : "本地模式";
    public string AccountSubLabel => IsLoggedIn
        ? _settings.Username ?? ""
        : "绑定服务器可跨网络传输";
    public string AccountInitial => string.IsNullOrWhiteSpace(AccountLabel)
        ? "L"
        : AccountLabel.Trim()[0].ToString().ToUpperInvariant();
    public string CurrentDeviceName => _settings.DeviceName;
    public string CurrentDeviceId => _settings.DeviceId.ToString();
    public string ReceiveDirectoryLabel => _settings.ReceiveDirectory;
    public string ChunkDetailLabel =>
        $"每块 {_selectedChunkSize.Label}，失败自动重试 3 次";
    public string ServerUrlLabel => ApiClient.NormalizeServerUrl(_settings.ServerUrl);
    public string DeviceIdShort => _settings.DeviceId.ToString()[..8].ToUpperInvariant();

    public string ServerStatus
    {
        get => _serverStatus;
        private set => SetProperty(ref _serverStatus, value);
    }

    public string ServerStatusColor
    {
        get => _serverStatusColor;
        private set => SetProperty(ref _serverStatusColor, value);
    }

    public string LanStatus
    {
        get => _lanStatus;
        private set => SetProperty(ref _lanStatus, value);
    }

    public string LanStatusColor
    {
        get => _lanStatusColor;
        private set => SetProperty(ref _lanStatusColor, value);
    }

    public string LocalIpv4
    {
        get => _localIpv4;
        private set => SetProperty(ref _localIpv4, value);
    }

    public string LocalIpv6
    {
        get => _localIpv6;
        private set => SetProperty(ref _localIpv6, value);
    }

    public string DirectStatus
    {
        get => _directStatus;
        private set => SetProperty(ref _directStatus, value);
    }

    public string DiscoveryStatus
    {
        get => _discoveryStatus;
        private set => SetProperty(ref _discoveryStatus, value);
    }

    public long LatencyMs { get; private set; }

    public int OnlineDeviceCount => Devices.Count(x => x.IsOnline);
    public int LanDeviceCount => Devices.Count(x => x.LanOnline);
    public int RelayDeviceCount => Devices.Count(x => x.ServerOnline);
    public int TransferCount => Transfers.Count;
    public int ActiveTransferCount => Transfers.Count(x => !x.IsTerminal);
    public int ShareCount => Shares.Count;
    public bool HasDevices => Devices.Count > 0;
    public bool HasNoDevices => Devices.Count == 0;
    public bool HasShares => Shares.Count > 0;
    public bool HasNoShares => Shares.Count == 0;
    public bool HasNoTransfers => Transfers.Count == 0;

    public PeerDevice? ComposePeer
    {
        get => _composePeer;
        private set => SetProperty(ref _composePeer, value);
    }

    public bool IsComposeOpen
    {
        get => _isComposeOpen;
        private set => SetProperty(ref _isComposeOpen, value);
    }

    public string ComposeText
    {
        get => _composeText;
        set
        {
            if (SetProperty(ref _composeText, value))
            {
                OnPropertyChanged(nameof(ComposeLengthLabel));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string ComposeLengthLabel => $"{ComposeText.Length} / 1,000,000";

    public string ComposeTargetLabel
    {
        get => _composeTargetLabel;
        private set => SetProperty(ref _composeTargetLabel, value);
    }

    public LocalTransferRecord? PendingInvite
    {
        get => _pendingInvite;
        private set
        {
            if (SetProperty(ref _pendingInvite, value))
            {
                OnPropertyChanged(nameof(IsInviteOpen));
            }
        }
    }

    public bool IsInviteOpen => PendingInvite is not null;

    public LocalTransferRecord? SelectedTransfer
    {
        get => _selectedTransfer;
        private set => SetProperty(ref _selectedTransfer, value);
    }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        private set => SetProperty(ref _isDetailOpen, value);
    }

    public bool IsShareDialogOpen
    {
        get => _isShareDialogOpen;
        private set => SetProperty(ref _isShareDialogOpen, value);
    }

    public bool IsCreatingShare
    {
        get => _isCreatingShare;
        private set
        {
            if (SetProperty(ref _isCreatingShare, value))
            {
                OnPropertyChanged(nameof(IsNotCreatingShare));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNotCreatingShare => !IsCreatingShare;

    public string ShareDialogTitle
    {
        get => _shareDialogTitle;
        private set => SetProperty(ref _shareDialogTitle, value);
    }

    public string ShareTargetLabel
    {
        get => _shareTargetLabel;
        private set => SetProperty(ref _shareTargetLabel, value);
    }

    public string ShareName
    {
        get => _shareName;
        set => SetProperty(ref _shareName, value);
    }

    public string ShareText
    {
        get => _shareText;
        set => SetProperty(ref _shareText, value);
    }

    public bool ShareIsText => _shareKind == TransferKinds.Text;

    public string SharePassword
    {
        get => _sharePassword;
        set => SetProperty(ref _sharePassword, value);
    }

    public string ShareExpiryHours
    {
        get => _shareExpiryHours;
        set => SetProperty(ref _shareExpiryHours, value);
    }

    public string ShareMaxDownloads
    {
        get => _shareMaxDownloads;
        set => SetProperty(ref _shareMaxDownloads, value);
    }

    public ShareItem? CreatedShare
    {
        get => _createdShare;
        private set
        {
            if (SetProperty(ref _createdShare, value))
            {
                OnPropertyChanged(nameof(HasCreatedShare));
            }
        }
    }

    public bool HasCreatedShare => CreatedShare is not null;

    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetProperty(ref _toastVisible, value);
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public string ToastColor => _toastIsError ? "#FF8795" : "#62E0D0";

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _directServer.StartAsync();
            await _discovery.StartAsync(_directServer.Port);
            UpdateDiscoveryState();
            if (IsLoggedIn)
            {
                try
                {
                    await _api.MeAsync();
                    await _api.RegisterDeviceAsync(_settings.DeviceName, Environment.MachineName);
                    await RefreshDevicesAsync();
                    await RefreshSharesAsync();
                    _relay.Start();
                }
                catch (ApiClientException ex) when (ex.StatusCode == 401)
                {
                    ClearAuthentication();
                    ShowToast("登录已过期，请重新绑定服务器。", true);
                }
                catch (Exception ex)
                {
                    ShowToast("服务器暂不可用：" + ex.Message, true);
                }
            }

            IsInitialized = true;
        }
        catch (Exception ex)
        {
            ShowToast("初始化失败：" + ex.Message, true);
            IsInitialized = true;
        }

        UpdateConnectionText();
        RaiseCounts();
    }

    private async Task HandleRelayMessageAsync(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeProperty))
        {
            return;
        }

        var type = typeProperty.GetString();
        if (type == WireEvents.Hello)
        {
            if (root.TryGetProperty("devices", out var devicesElement))
            {
                var devices = devicesElement.Deserialize<List<DeviceDto>>(LumenJson.Options);
                if (devices is not null)
                {
                    await UiThread.InvokeAsync(() => MergeServerDevices(devices));
                }
            }

            return;
        }

        if (type is WireEvents.DeviceOnline or WireEvents.DeviceOffline)
        {
            await RefreshDevicesAsync();
            return;
        }

        await _coordinator.HandleRelayEventAsync(root);
    }

    private async Task ConnectAsync()
    {
        if (IsAuthenticating)
        {
            return;
        }

        IsAuthenticating = true;
        try
        {
            var normalized = ApiClient.NormalizeServerUrl(ServerUrl);
            ServerUrl = normalized;
            _settings.ServerUrl = normalized;
            await _api.HealthAsync(normalized);
            var auth = await _api.LoginAsync(UsernameInput.Trim(), PasswordInput);
            await ApplyAuthenticationAsync(auth);
            ShowToast("服务器已连接，设备已自动绑定。");
        }
        catch (Exception ex)
        {
            ShowToast("登录失败：" + ex.Message, true);
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    private async Task RegisterAsync()
    {
        if (IsAuthenticating)
        {
            return;
        }

        IsAuthenticating = true;
        try
        {
            var normalized = ApiClient.NormalizeServerUrl(ServerUrl);
            ServerUrl = normalized;
            _settings.ServerUrl = normalized;
            await _api.HealthAsync(normalized);
            var displayName = string.IsNullOrWhiteSpace(DisplayNameInput)
                ? UsernameInput.Trim()
                : DisplayNameInput.Trim();
            var auth = await _api.RegisterAsync(
                UsernameInput.Trim(),
                PasswordInput,
                displayName);
            await ApplyAuthenticationAsync(auth);
            ShowToast("账户已创建，这台设备已自动绑定。");
        }
        catch (Exception ex)
        {
            ShowToast("注册失败：" + ex.Message, true);
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    private async Task ApplyAuthenticationAsync(AuthResponse auth)
    {
        _settings.AuthToken = auth.Token;
        _settings.TokenExpiresAt = auth.ExpiresAt;
        _settings.UserId = auth.User.Id;
        _settings.Username = auth.User.Username;
        _settings.DisplayName = auth.User.DisplayName;
        _settings.ServerUrl = ApiClient.NormalizeServerUrl(ServerUrl);
        PasswordInput = "";
        UsernameInput = auth.User.Username;
        DisplayNameInput = auth.User.DisplayName;
        await _settingsStore.SaveAsync();
        await _api.RegisterDeviceAsync(_settings.DeviceName, Environment.MachineName);
        await _relay.RestartAsync();
        await RefreshDevicesAsync();
        await RefreshSharesAsync();
        RaiseAuthProperties();
        UpdateConnectionText();
    }

    private async Task LogoutAsync()
    {
        await _api.LogoutAsync();
        ClearAuthentication();
        await _relay.StopAsync();
        Shares.Clear();
        await RefreshDevicesAsync();
        RaiseCounts();
        ShowToast("已退出服务器账户，仍可使用局域网传输。");
    }

    private void ClearAuthentication()
    {
        _settings.AuthToken = null;
        _settings.TokenExpiresAt = null;
        _settings.UserId = null;
        _settings.Username = null;
        _settings.DisplayName = null;
        _ = _settingsStore.SaveAsync();
        RaiseAuthProperties();
    }

    private async Task RefreshAllAsync()
    {
        await RefreshDevicesAsync();
        if (IsLoggedIn)
        {
            await RefreshSharesAsync();
        }

        if (!_discovery.IsRunning)
        {
            await _discovery.StartAsync(_directServer.Port);
        }

        UpdateDiscoveryState();
        ShowToast("已刷新设备与连接状态。");
    }

    private async Task RefreshDevicesAsync()
    {
        if (IsLoggedIn)
        {
            await _refreshLock.WaitAsync();
            try
            {
                var devices = await _api.GetDevicesAsync();
                await UiThread.InvokeAsync(() => MergeServerDevices(devices));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowToast("刷新已绑定设备失败：" + ex.Message, true);
            }
            finally
            {
                _refreshLock.Release();
            }
        }
    }

    private void MergeServerDevices(IReadOnlyList<DeviceDto> devices)
    {
        var incomingIds = new HashSet<Guid>();
        foreach (var dto in devices)
        {
            if (dto.Id == _settings.DeviceId)
            {
                continue;
            }

            incomingIds.Add(dto.Id);
            if (!_deviceMap.TryGetValue(dto.Id, out var peer))
            {
                peer = new PeerDevice { Id = dto.Id };
                _deviceMap[dto.Id] = peer;
                Devices.Add(peer);
            }

            peer.ApplyServer(dto);
        }

        foreach (var stale in _deviceMap.Values.Where(x => x.IsKnownOnServer && !incomingIds.Contains(x.Id)).ToList())
        {
            stale.IsKnownOnServer = false;
            stale.ServerOnline = false;
            if (!stale.LanOnline)
            {
                Devices.Remove(stale);
                _deviceMap.Remove(stale.Id);
            }
        }

        _serverDeviceIds.Clear();
        _serverDeviceIds.UnionWith(incomingIds);
        ReorderDevices();
        RaiseCounts();
    }

    private void MergeLanPeer(DiscoveryPacket packet)
    {
        if (packet.DeviceId == _settings.DeviceId)
        {
            return;
        }

        if (!_deviceMap.TryGetValue(packet.DeviceId, out var peer))
        {
            peer = new PeerDevice { Id = packet.DeviceId };
            _deviceMap[packet.DeviceId] = peer;
            Devices.Add(peer);
        }

        peer.ApplyLan(packet);
        ReorderDevices();
        RaiseCounts();
        UpdateConnectionText();
    }

    private void MarkLanPeerLost(Guid id)
    {
        if (!_deviceMap.TryGetValue(id, out var peer))
        {
            return;
        }

        peer.MarkLanExpired();
        if (!peer.IsKnownOnServer)
        {
            Devices.Remove(peer);
            _deviceMap.Remove(id);
        }

        RaiseCounts();
        UpdateConnectionText();
    }

    private void ReorderDevices()
    {
        var sorted = Devices
            .OrderByDescending(x => x.LanOnline)
            .ThenByDescending(x => x.ServerOnline)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (var index = 0; index < sorted.Count; index++)
        {
            var currentIndex = Devices.IndexOf(sorted[index]);
            if (currentIndex != index)
            {
                Devices.Move(currentIndex, index);
            }
        }
    }

    private async Task SendFilesAsync(PeerDevice? peer)
    {
        if (peer is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"发送文件到 {peer.Name}",
            Multiselect = true,
            Filter = "所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Navigate("transfers");
        await _coordinator.SendFilesAsync(peer, dialog.FileNames);
    }

    private async Task SendImagesAsync(PeerDevice? peer)
    {
        if (peer is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"发送图片到 {peer.Name}",
            Multiselect = true,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.avif;*.bmp"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Navigate("transfers");
        await _coordinator.SendFilesAsync(peer, dialog.FileNames);
    }

    private void OpenCompose(object? parameter)
    {
        if (parameter is not PeerDevice peer)
        {
            return;
        }

        ComposePeer = peer;
        ComposeTargetLabel = peer.Name;
        ComposeText = "";
        IsComposeOpen = true;
    }

    private async Task SendClipboardAsync(PeerDevice? peer)
    {
        if (peer is null)
        {
            return;
        }

        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("剪贴板中没有可用文字。", true);
            return;
        }

        Navigate("transfers");
        await _coordinator.SendTextAsync(peer, text);
    }

    private async Task SendComposeAsync()
    {
        if (ComposePeer is null || string.IsNullOrWhiteSpace(ComposeText))
        {
            return;
        }

        var peer = ComposePeer;
        var text = ComposeText;
        IsComposeOpen = false;
        Navigate("transfers");
        await _coordinator.SendTextAsync(peer, text);
    }

    private void PasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                ComposeText = Clipboard.GetText();
            }
        }
        catch (Exception ex)
        {
            ShowToast("读取剪贴板失败：" + ex.Message, true);
        }
    }

    private async Task CancelTransferAsync(object? parameter)
    {
        if (parameter is not LocalTransferRecord record)
        {
            return;
        }

        await _coordinator.CancelAsync(record);
        ShowToast("已请求取消传输。");
    }

    private static void OpenFile(LocalTransferRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.LocalPath) || !File.Exists(record.LocalPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(record.LocalPath) { UseShellExecute = true });
    }

    private void OpenFolder(LocalTransferRecord? record)
    {
        if (record is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(record.LocalPath) && File.Exists(record.LocalPath))
        {
            Process.Start("explorer.exe", $"/select,\"{record.LocalPath}\"");
        }
        else
        {
            OpenPath(_settings.ReceiveDirectory);
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast("无法打开目录：" + ex.Message, true);
        }
    }

    private void EnqueueInvite(LocalTransferRecord record)
    {
        if (PendingInvite is null)
        {
            PendingInvite = record;
        }
        else if (record.Id != PendingInvite.Id)
        {
            _inviteQueue.Enqueue(record);
        }
    }

    private async Task AcceptInviteAsync()
    {
        if (PendingInvite is not { } invite)
        {
            return;
        }

        try
        {
            await _coordinator.AcceptInviteAsync(invite);
            ShowToast(invite.IsLan ? "已接受局域网传输。" : "已接受中转传输，正在准备接收。");
        }
        catch (Exception ex)
        {
            ShowToast("接受传输失败：" + ex.Message, true);
        }
        finally
        {
            AdvanceInvite();
        }
    }

    private async Task DeclineInviteAsync()
    {
        if (PendingInvite is not { } invite)
        {
            return;
        }

        try
        {
            await _coordinator.DeclineInviteAsync(invite);
            ShowToast("已拒绝传输。");
        }
        catch (Exception ex)
        {
            ShowToast("拒绝传输失败：" + ex.Message, true);
        }
        finally
        {
            AdvanceInvite();
        }
    }

    private void AdvanceInvite()
    {
        PendingInvite = _inviteQueue.Count > 0 ? _inviteQueue.Dequeue() : null;
    }

    private async Task RefreshSharesAsync()
    {
        if (!IsLoggedIn)
        {
            Shares.Clear();
            RaiseCounts();
            return;
        }

        try
        {
            var items = await _api.GetSharesAsync();
            await UiThread.InvokeAsync(() =>
            {
                Shares.Clear();
                foreach (var item in items.OrderByDescending(x => x.CreatedAt))
                {
                    Shares.Add(new ShareItem(item));
                }
            });
            RaiseCounts();
        }
        catch (Exception ex)
        {
            ShowToast("加载分享失败：" + ex.Message, true);
        }
    }

    private void ChooseShareFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要分享的文件",
            Filter = "所有文件|*.*|图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf"
        };
        if (dialog.ShowDialog() == true)
        {
            OpenShareDialog(
                Path.GetFileName(dialog.FileName),
                dialog.FileName,
                FileUtilities.GetMimeType(dialog.FileName),
                null);
        }
    }

    private void OpenTextShare()
    {
        OpenShareDialog("文字分享", null, "text/plain; charset=utf-8", "");
    }

    private void ShareTransfer(LocalTransferRecord? record)
    {
        if (record is null)
        {
            return;
        }

        if (record.Kind == TransferKinds.Text)
        {
            OpenShareDialog(
                string.IsNullOrWhiteSpace(record.Name) ? "文字分享" : record.Name,
                null,
                "text/plain; charset=utf-8",
                record.TextContent ?? "");
        }
        else if (!string.IsNullOrWhiteSpace(record.LocalPath) && File.Exists(record.LocalPath))
        {
            OpenShareDialog(
                record.Name,
                record.LocalPath,
                record.MimeType,
                null);
        }
        else
        {
            ShowToast("本地文件不存在，无法创建分享。", true);
        }
    }

    private void OpenShareDialog(
        string name,
        string? filePath,
        string mime,
        string? text)
    {
        if (!IsLoggedIn)
        {
            ShowToast("请先在设置中绑定服务器，再创建公开分享。", true);
            Navigate("settings");
            return;
        }

        _shareFilePath = filePath;
        _shareKind = text is not null
            ? TransferKinds.Text
            : mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? TransferKinds.Image
                : TransferKinds.File;
        _shareText = text ?? "";
        ShareName = name;
        ShareText = _shareText;
        ShareTargetLabel = filePath is not null
            ? $"{Formatters.Bytes(new FileInfo(filePath).Length)} · {filePath}"
            : "文字内容 · 上限 1 MiB";
        ShareDialogTitle = "创建分享链接";
        SharePassword = "";
        ShareExpiryHours = "24";
        ShareMaxDownloads = "0";
        CreatedShare = null;
        IsCreatingShare = false;
        IsShareDialogOpen = true;
        OnPropertyChanged(nameof(ShareIsText));
    }

    private async Task CreateShareAsync()
    {
        if (IsCreatingShare)
        {
            return;
        }

        if (!int.TryParse(ShareExpiryHours, out var hours) || hours is < 1 or > 8760)
        {
            ShowToast("有效期需为 1 到 8760 小时。", true);
            return;
        }

        if (!int.TryParse(ShareMaxDownloads, out var maxDownloads) ||
            maxDownloads is < 0 or > 1_000_000)
        {
            ShowToast("下载次数上限无效。", true);
            return;
        }

        if (!string.IsNullOrEmpty(SharePassword) && SharePassword.Length is < 4 or > 64)
        {
            ShowToast("分享密码需为 4 到 64 个字符。", true);
            return;
        }

        IsCreatingShare = true;
        try
        {
            ShareDto dto;
            if (ShareIsText)
            {
                if (string.IsNullOrWhiteSpace(ShareText))
                {
                    throw new InvalidOperationException("文字内容不能为空。");
                }

                dto = await _api.CreateTextShareAsync(
                    ShareName,
                    ShareText,
                    hours,
                    string.IsNullOrWhiteSpace(SharePassword) ? null : SharePassword,
                    maxDownloads);
            }
            else
            {
                if (_shareFilePath is null || !File.Exists(_shareFilePath))
                {
                    throw new FileNotFoundException("分享文件不存在。", _shareFilePath);
                }

                dto = await _api.CreateFileShareAsync(
                    _shareFilePath,
                    ShareName,
                    hours,
                    string.IsNullOrWhiteSpace(SharePassword) ? null : SharePassword,
                    maxDownloads);
            }

            var item = new ShareItem(dto);
            Shares.Insert(0, item);
            CreatedShare = item;
            CopyText(item.Url);
            RaiseCounts();
            ShowToast("分享已创建，链接已复制。");
        }
        catch (Exception ex)
        {
            ShowToast("创建分享失败：" + ex.Message, true);
        }
        finally
        {
            IsCreatingShare = false;
        }
    }

    private void CloseShareDialog()
    {
        IsShareDialogOpen = false;
        CreatedShare = null;
        _shareFilePath = null;
        _shareText = "";
        SharePassword = "";
    }

    private async Task RevokeShareAsync(ShareItem? item)
    {
        if (item is null)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确定撤销“{item.Name}”的分享链接？",
            "撤销分享",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _api.RevokeShareAsync(item.Id);
            Shares.Remove(item);
            RaiseCounts();
            ShowToast("分享已撤销。");
        }
        catch (Exception ex)
        {
            ShowToast("撤销失败：" + ex.Message, true);
        }
    }

    private void ChooseReceiveFolder()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择接收文件保存目录",
                InitialDirectory = Directory.Exists(ReceiveDirectory)
                    ? ReceiveDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                ReceiveDirectory = dialog.FolderName;
            }
        }
        catch (Exception ex)
        {
            ShowToast("选择目录失败：" + ex.Message, true);
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var oldServer = ApiClient.NormalizeServerUrl(_settings.ServerUrl);
            var newServer = ApiClient.NormalizeServerUrl(ServerUrl);
            var serverChanged = !string.Equals(oldServer, newServer, StringComparison.OrdinalIgnoreCase);
            _settings.ServerUrl = newServer;
            _settings.DeviceName = string.IsNullOrWhiteSpace(DeviceName)
                ? Environment.MachineName
                : DeviceName.Trim();
            _settings.ReceiveDirectory = string.IsNullOrWhiteSpace(ReceiveDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lumen")
                : ReceiveDirectory.Trim();
            _settings.ChunkSize = SelectedChunkSize.Bytes;
            Directory.CreateDirectory(_settings.ReceiveDirectory);
            if (serverChanged)
            {
                ClearAuthentication();
                await _relay.StopAsync();
            }

            await _settingsStore.SaveAsync();
            if (IsLoggedIn)
            {
                await _api.RegisterDeviceAsync(_settings.DeviceName, Environment.MachineName);
                await _relay.RestartAsync();
            }

            OnPropertiesChanged(
                nameof(CurrentDeviceName),
                nameof(ReceiveDirectoryLabel),
                nameof(ServerUrlLabel),
                nameof(ChunkDetailLabel),
                nameof(AccountLabel));
            ShowToast(serverChanged
                ? "设置已保存；服务器地址变更后需重新登录。"
                : "设置已保存。");
        }
        catch (Exception ex)
        {
            ShowToast("保存设置失败：" + ex.Message, true);
        }
    }

    private async Task PersistThemeAsync()
    {
        try
        {
            await _settingsStore.SaveAsync();
        }
        catch
        {
            // Theme changes apply immediately; persistence can be retried on the next save.
        }
    }

    private void UpdateDiscoveryState()
    {
        LocalAddresses.Clear();
        foreach (var address in _discovery.LocalAddresses)
        {
            LocalAddresses.Add(address);
        }

        var ipv4 = _discovery.LocalAddresses.Where(x => x.Family == "IPv4").Select(x => x.Address).ToList();
        var ipv6 = _discovery.LocalAddresses.Where(x => x.Family == "IPv6").Select(x => x.Address).ToList();
        LocalIpv4 = ipv4.Count == 0 ? "未获取到 IPv4" : string.Join(" · ", ipv4.Take(2));
        LocalIpv6 = ipv6.Count == 0 ? "未获取到 IPv6" : string.Join(" · ", ipv6.Take(2));
        DirectStatus = _directServer.IsRunning
            ? $"监听 0.0.0.0:{_directServer.Port} · 双栈"
            : "直连服务未启动";
        DiscoveryStatus = _discovery.IsRunning
            ? $"UDP {LumenProtocol.DiscoveryPort} · 每 3 秒广播 · {_discovery.SeenPeerCount} 台可见"
            : _discovery.LastError ?? "局域网发现未启动";
        LanStatus = _discovery.IsRunning
            ? $"局域网发现中 · {LanDeviceCount} 台"
            : "局域网发现不可用";
        LanStatusColor = _discovery.IsRunning ? "#55DCA8" : "#FF8795";
        OnPropertiesChanged(
            nameof(LocalIpv4),
            nameof(LocalIpv6),
            nameof(DirectStatus),
            nameof(DiscoveryStatus),
            nameof(LanStatus),
            nameof(LanStatusColor));
    }

    private void UpdateConnectionText()
    {
        if (_relay.IsConnected)
        {
            ServerStatus = LatencyMs > 0
                ? $"服务器已连接 · {LatencyMs} ms"
                : "服务器已连接";
            ServerStatusColor = "#55DCA8";
        }
        else if (IsLoggedIn)
        {
            ServerStatus = "服务器重连中…";
            ServerStatusColor = "#F1BD69";
        }
        else
        {
            ServerStatus = "未绑定服务器";
            ServerStatusColor = "#7B849E";
        }

        OnPropertiesChanged(
            nameof(ServerStatus),
            nameof(ServerStatusColor),
            nameof(OnlineDeviceCount),
            nameof(LanDeviceCount),
            nameof(RelayDeviceCount));
    }

    private void RelayOnConnectionChanged(object? sender, bool connected)
    {
        UiThread.Post(() =>
        {
            UpdateConnectionText();
            if (connected)
            {
                _ = RefreshDevicesAsync();
            }
        });
    }

    private void RaiseCounts()
    {
        OnPropertiesChanged(
            nameof(OnlineDeviceCount),
            nameof(LanDeviceCount),
            nameof(RelayDeviceCount),
            nameof(TransferCount),
            nameof(ActiveTransferCount),
            nameof(ShareCount),
            nameof(HasDevices),
            nameof(HasNoDevices),
            nameof(HasShares),
            nameof(HasNoShares),
            nameof(HasNoTransfers));
    }

    private void RaiseTransferSummary() => RaiseCounts();

    private void RaiseAuthProperties()
    {
        OnPropertiesChanged(
            nameof(IsLoggedIn),
            nameof(AccountLabel),
            nameof(AccountSubLabel),
            nameof(AccountInitial),
            nameof(ServerUrlLabel),
            nameof(DeviceIdShort));
        UpdateConnectionText();
    }

    private bool FilterTransfer(object item)
    {
        if (item is not LocalTransferRecord record)
        {
            return false;
        }

        return TransferFilter switch
        {
            "out" => record.IsOutgoing,
            "in" => record.IsIncoming,
            "lan" => record.IsLan,
            "relay" => record.IsRelay,
            _ => true
        };
    }

    private void Navigate(string page)
    {
        CurrentPage = page switch
        {
            "transfers" or "shares" or "settings" => page,
            _ => "devices"
        };
        if (page == "shares" && IsLoggedIn)
        {
            _ = RefreshSharesAsync();
        }
    }

    private void ShowToast(string message, bool isError = false)
    {
        UiThread.Post(() =>
        {
            ToastMessage = message;
            _toastIsError = isError;
            ToastVisible = true;
            OnPropertyChanged(nameof(ToastColor));
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    private void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            ShowToast("已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            ShowToast("复制失败：" + ex.Message, true);
        }
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast("无法打开链接：" + ex.Message, true);
        }
    }

    private AsyncRelayCommand Async(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(
            _ => execute(),
            _ => canExecute?.Invoke() ?? true,
            ex => ShowToast(ex.Message, true));

    private AsyncRelayCommand Async(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null) =>
        new(execute, canExecute, ex => ShowToast(ex.Message, true));

    public void Dispose()
    {
        _toastTimer.Stop();
        _relay.ConnectionChanged -= RelayOnConnectionChanged;
        _refreshLock.Dispose();
    }
}
