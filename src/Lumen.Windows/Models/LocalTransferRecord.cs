using System.Text.Json.Serialization;
using Lumen.Windows.Infrastructure;
using Lumen.Shared;

namespace Lumen.Windows.Models;

public sealed class LocalTransferRecord : ObservableObject
{
    private Guid _id;
    private string _direction = "out";
    private string _route = TransferRoutes.Relay;
    private string _status = TransferStatuses.Pending;
    private string _kind = TransferKinds.File;
    private string _name = "";
    private string _mimeType = "application/octet-stream";
    private long _size;
    private string? _sha256;
    private int _chunkSize;
    private int _totalChunks;
    private int _completedChunks;
    private long _transferredBytes;
    private double _speedBps;
    private string _peerName = "";
    private string? _peerDeviceId;
    private string? _peerPlatform;
    private string? _localAddress;
    private string? _remoteAddress;
    private string? _serverAddress;
    private string? _ipFamily;
    private string? _localPath;
    private string? _textContent;
    private string? _error;
    private DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _completedAt;
    private List<TransferEventRecord> _events = [];

    public Guid Id { get => _id; set => SetProperty(ref _id, value); }
    public string Direction { get => _direction; set { if (SetProperty(ref _direction, value)) NotifyDerived(); } }
    public string Route { get => _route; set { if (SetProperty(ref _route, value)) NotifyDerived(); } }
    public string Status { get => _status; set { if (SetProperty(ref _status, value)) NotifyDerived(); } }
    public string Kind { get => _kind; set { if (SetProperty(ref _kind, value)) NotifyDerived(); } }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) NotifyDerived(); } }
    public string MimeType { get => _mimeType; set { if (SetProperty(ref _mimeType, value)) NotifyDerived(); } }
    public long Size { get => _size; set { if (SetProperty(ref _size, value)) NotifyDerived(); } }
    public string? Sha256 { get => _sha256; set => SetProperty(ref _sha256, value); }
    public int ChunkSize { get => _chunkSize; set { if (SetProperty(ref _chunkSize, value)) NotifyDerived(); } }
    public int TotalChunks { get => _totalChunks; set { if (SetProperty(ref _totalChunks, value)) NotifyDerived(); } }
    public int CompletedChunks { get => _completedChunks; set { if (SetProperty(ref _completedChunks, value)) NotifyDerived(); } }
    public long TransferredBytes { get => _transferredBytes; set { if (SetProperty(ref _transferredBytes, value)) NotifyDerived(); } }
    public double SpeedBps { get => _speedBps; set { if (SetProperty(ref _speedBps, value)) NotifyDerived(); } }
    public string PeerName { get => _peerName; set { if (SetProperty(ref _peerName, value)) NotifyDerived(); } }
    public string? PeerDeviceId { get => _peerDeviceId; set => SetProperty(ref _peerDeviceId, value); }
    public string? PeerPlatform { get => _peerPlatform; set { if (SetProperty(ref _peerPlatform, value)) NotifyDerived(); } }
    public string? LocalAddress { get => _localAddress; set { if (SetProperty(ref _localAddress, value)) NotifyDerived(); } }
    public string? RemoteAddress { get => _remoteAddress; set { if (SetProperty(ref _remoteAddress, value)) NotifyDerived(); } }
    public string? ServerAddress { get => _serverAddress; set { if (SetProperty(ref _serverAddress, value)) NotifyDerived(); } }
    public string? IpFamily { get => _ipFamily; set { if (SetProperty(ref _ipFamily, value)) NotifyDerived(); } }
    public string? LocalPath { get => _localPath; set { if (SetProperty(ref _localPath, value)) NotifyDerived(); } }
    public string? TextContent { get => _textContent; set { if (SetProperty(ref _textContent, value)) NotifyDerived(); } }
    public string? Error { get => _error; set { if (SetProperty(ref _error, value)) NotifyDerived(); } }
    public DateTimeOffset CreatedAt { get => _createdAt; set { if (SetProperty(ref _createdAt, value)) NotifyDerived(); } }
    public DateTimeOffset UpdatedAt { get => _updatedAt; set { if (SetProperty(ref _updatedAt, value)) NotifyDerived(); } }
    public DateTimeOffset? CompletedAt { get => _completedAt; set { if (SetProperty(ref _completedAt, value)) NotifyDerived(); } }
    public List<TransferEventRecord> Events
    {
        get => _events;
        set
        {
            if (SetProperty(ref _events, value ?? []))
            {
                OnPropertyChanged(nameof(EventCount));
                OnPropertyChanged(nameof(LastEventLabel));
            }
        }
    }

    private readonly object _eventLock = new();

    [JsonIgnore] public bool IsIncoming => Direction == "in";
    [JsonIgnore] public bool IsOutgoing => Direction == "out";
    [JsonIgnore] public bool IsLan => Route == TransferRoutes.Lan;
    [JsonIgnore] public bool IsRelay => Route == TransferRoutes.Relay;
    [JsonIgnore] public bool IsTerminal => TransferStatuses.IsTerminal(Status);
    [JsonIgnore] public bool IsPending => Status == TransferStatuses.Pending;
    [JsonIgnore] public bool CanCancel => !IsTerminal;
    [JsonIgnore] public bool CanAccept => IsIncoming && Status == TransferStatuses.Pending;
    [JsonIgnore] public bool CanDecline => IsIncoming && Status == TransferStatuses.Pending;
    [JsonIgnore] public bool CanShare => IsTerminal && Status == TransferStatuses.Completed &&
                                         (!string.IsNullOrWhiteSpace(LocalPath) || Kind == TransferKinds.Text);
    [JsonIgnore] public bool CanOpen => !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);
    [JsonIgnore] public bool IsImage => FileUtilities.IsImage(MimeType, LocalPath ?? Name);
    [JsonIgnore] public bool IsText => Kind == TransferKinds.Text;
    [JsonIgnore] public string DirectionLabel => IsIncoming ? "接收" : "发送";
    [JsonIgnore] public string DirectionGlyph => IsIncoming ? "\uE896" : "\uE898";
    [JsonIgnore] public string RouteLabel => IsLan ? "局域网直连" : "服务器中转";
    [JsonIgnore] public string RouteShortLabel => IsLan ? "局域网" : "中转";
    [JsonIgnore] public string RouteGlyph => IsLan ? "\uE968" : "\uE753";
    [JsonIgnore] public string StatusLabel => Status switch
    {
        TransferStatuses.Pending => "等待确认",
        TransferStatuses.Accepted => "已接受",
        TransferStatuses.Uploading => IsIncoming ? "接收中" : "发送中",
        TransferStatuses.Ready => IsIncoming ? "等待下载" : "等待接收方完成",
        TransferStatuses.Completed => "已完成",
        TransferStatuses.Declined => "已拒绝",
        TransferStatuses.Cancelled => "已取消",
        TransferStatuses.Failed => "失败",
        _ => Status
    };
    [JsonIgnore] public string StatusColor => Status switch
    {
        TransferStatuses.Completed => "#55DCA8",
        TransferStatuses.Failed or TransferStatuses.Declined or TransferStatuses.Cancelled => "#FF8795",
        TransferStatuses.Ready => "#62E0D0",
        TransferStatuses.Pending => "#F1BD69",
        TransferStatuses.Accepted or TransferStatuses.Uploading => "#9B92FF",
        _ => "#98A1BA"
    };
    [JsonIgnore] public string KindGlyph => Kind switch
    {
        TransferKinds.Image => "\uEB9F",
        TransferKinds.Text => "\uE8A5",
        _ => "\uE7C3"
    };
    [JsonIgnore] public string KindLabel => Kind switch
    {
        TransferKinds.Image => "图片",
        TransferKinds.Text => "文字",
        _ => "文件"
    };
    [JsonIgnore] public string PeerLabel => string.IsNullOrWhiteSpace(PeerName) ? "未知设备" : PeerName;
    [JsonIgnore] public string PeerInitial =>
        string.IsNullOrWhiteSpace(PeerName) ? "?" : PeerName.Trim()[0].ToString().ToUpperInvariant();
    [JsonIgnore] public string SizeLabel => Formatters.Bytes(Size);
    [JsonIgnore] public string TransferredLabel => $"{Formatters.Bytes(TransferredBytes)} / {Formatters.Bytes(Size)}";
    [JsonIgnore] public string SpeedLabel => Formatters.Speed(SpeedBps);
    [JsonIgnore] public string TimeLabel => Formatters.Time(CreatedAt);
    [JsonIgnore] public string FullTimeLabel => Formatters.FullTime(CreatedAt);
    [JsonIgnore] public string UpdatedTimeLabel => Formatters.FullTime(UpdatedAt);
    [JsonIgnore] public string CompletedTimeLabel => CompletedAt is { } completed ? Formatters.FullTime(completed) : "—";
    [JsonIgnore] public string ProgressText => $"{ProgressPercent:0}%";
    [JsonIgnore] public double ProgressPercent =>
        Status == TransferStatuses.Completed && Size == 0 ? 100 :
        Size <= 0 ? 0 :
        Math.Clamp(TransferredBytes * 100d / Size, 0, 100);
    [JsonIgnore] public string ChunkLabel =>
        ChunkSize <= 0 ? "文字内容不分块" :
        $"{Formatters.Bytes(ChunkSize)} / 块 · {CompletedChunks}/{TotalChunks} 块";
    [JsonIgnore] public string IpLabel => string.IsNullOrWhiteSpace(IpFamily) ? "IP 未知" : IpFamily;
    [JsonIgnore] public string AddressLabel
    {
        get
        {
            if (IsLan)
            {
                return string.IsNullOrWhiteSpace(RemoteAddress)
                    ? LocalAddress ?? "局域网地址未知"
                    : $"{LocalAddress ?? "本机"} ↔ {RemoteAddress}";
            }

            return string.IsNullOrWhiteSpace(ServerAddress)
                ? RemoteAddress ?? "服务器中转"
                : $"经 {ServerAddress} 中转";
        }
    }
    [JsonIgnore] public string ConnectionDetail =>
        $"{RouteShortLabel} · {IpLabel} · {AddressLabel}";
    [JsonIgnore] public string HashShort =>
        string.IsNullOrWhiteSpace(Sha256) ? "未提供" : Sha256[..Math.Min(16, Sha256.Length)] + "…";
    [JsonIgnore] public int EventCount => Events.Count;
    [JsonIgnore] public string LastEventLabel =>
        Events.Count == 0 ? "暂无事件" : Events[^1].Detail;
    [JsonIgnore] public string TextPreview =>
        string.IsNullOrWhiteSpace(TextContent)
            ? "（空）"
            : TextContent.Replace("\r", "").Replace("\n", " · ");
    [JsonIgnore] public string LocalPathLabel =>
        string.IsNullOrWhiteSpace(LocalPath) ? "尚未保存到本地" : LocalPath;
    [JsonIgnore] public string ErrorLabel => string.IsNullOrWhiteSpace(Error) ? "" : Error;

    public void AddEvent(TransferEventRecord item)
    {
        lock (_eventLock)
        {
            var copy = new List<TransferEventRecord>(Events) { item };
            UiThread.Post(() =>
            {
                Events = copy;
                UpdatedAt = item.At;
            });
        }
    }

    private void NotifyDerived()
    {
        OnPropertiesChanged(
            nameof(IsIncoming),
            nameof(IsOutgoing),
            nameof(IsLan),
            nameof(IsRelay),
            nameof(IsTerminal),
            nameof(IsPending),
            nameof(CanCancel),
            nameof(CanAccept),
            nameof(CanDecline),
            nameof(CanShare),
            nameof(CanOpen),
            nameof(IsImage),
            nameof(IsText),
            nameof(DirectionLabel),
            nameof(DirectionGlyph),
            nameof(RouteLabel),
            nameof(RouteShortLabel),
            nameof(RouteGlyph),
            nameof(StatusLabel),
            nameof(StatusColor),
            nameof(KindGlyph),
            nameof(KindLabel),
            nameof(PeerLabel),
            nameof(PeerInitial),
            nameof(SizeLabel),
            nameof(TransferredLabel),
            nameof(SpeedLabel),
            nameof(TimeLabel),
            nameof(FullTimeLabel),
            nameof(UpdatedTimeLabel),
            nameof(CompletedTimeLabel),
            nameof(ProgressText),
            nameof(ProgressPercent),
            nameof(ChunkLabel),
            nameof(IpLabel),
            nameof(AddressLabel),
            nameof(ConnectionDetail),
            nameof(HashShort),
            nameof(LastEventLabel),
            nameof(TextPreview),
            nameof(LocalPathLabel),
            nameof(ErrorLabel));
    }

    public LocalTransferRecord Snapshot() => new()
    {
        Id = Id,
        Direction = Direction,
        Route = Route,
        Status = Status,
        Kind = Kind,
        Name = Name,
        MimeType = MimeType,
        Size = Size,
        Sha256 = Sha256,
        ChunkSize = ChunkSize,
        TotalChunks = TotalChunks,
        CompletedChunks = CompletedChunks,
        TransferredBytes = TransferredBytes,
        SpeedBps = SpeedBps,
        PeerName = PeerName,
        PeerDeviceId = PeerDeviceId,
        PeerPlatform = PeerPlatform,
        LocalAddress = LocalAddress,
        RemoteAddress = RemoteAddress,
        ServerAddress = ServerAddress,
        IpFamily = IpFamily,
        LocalPath = LocalPath,
        TextContent = TextContent,
        Error = Error,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        CompletedAt = CompletedAt,
        Events = Events.ToList()
    };
}
