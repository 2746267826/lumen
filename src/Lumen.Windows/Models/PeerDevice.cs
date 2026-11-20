using System.Text.Json.Serialization;
using Lumen.Windows.Infrastructure;

namespace Lumen.Windows.Models;

public sealed class PeerDevice : ObservableObject
{
    private Guid _id;
    private string _name = "";
    private string _platform = "";
    private string? _model;
    private bool _serverOnline;
    private bool _lanOnline;
    private int _lanPort;
    private List<string> _lanAddresses = [];
    private List<string> _lanFamilies = [];
    private string? _serverIp;
    private string? _serverIpFamily;
    private DateTimeOffset _lastSeenAt;
    private DateTimeOffset _lanLastSeen;
    private bool _isKnownOnServer;
    private string? _relayUrl;
    private string? _appVersion;

    public Guid Id { get => _id; set { if (SetProperty(ref _id, value)) NotifyDerived(); } }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) NotifyDerived(); } }
    public string Platform { get => _platform; set { if (SetProperty(ref _platform, value)) NotifyDerived(); } }
    public string? Model { get => _model; set { if (SetProperty(ref _model, value)) NotifyDerived(); } }
    public bool ServerOnline { get => _serverOnline; set { if (SetProperty(ref _serverOnline, value)) NotifyDerived(); } }
    public bool LanOnline { get => _lanOnline; set { if (SetProperty(ref _lanOnline, value)) NotifyDerived(); } }
    public int LanPort { get => _lanPort; set => SetProperty(ref _lanPort, value); }
    public List<string> LanAddresses { get => _lanAddresses; set { if (SetProperty(ref _lanAddresses, value ?? [])) NotifyDerived(); } }
    public List<string> LanFamilies { get => _lanFamilies; set { if (SetProperty(ref _lanFamilies, value ?? [])) NotifyDerived(); } }
    public string? ServerIp { get => _serverIp; set { if (SetProperty(ref _serverIp, value)) NotifyDerived(); } }
    public string? ServerIpFamily { get => _serverIpFamily; set { if (SetProperty(ref _serverIpFamily, value)) NotifyDerived(); } }
    public DateTimeOffset LastSeenAt { get => _lastSeenAt; set { if (SetProperty(ref _lastSeenAt, value)) NotifyDerived(); } }
    public DateTimeOffset LanLastSeen { get => _lanLastSeen; set { if (SetProperty(ref _lanLastSeen, value)) NotifyDerived(); } }
    public bool IsKnownOnServer { get => _isKnownOnServer; set { if (SetProperty(ref _isKnownOnServer, value)) NotifyDerived(); } }
    public string? RelayUrl { get => _relayUrl; set => SetProperty(ref _relayUrl, value); }
    public string? AppVersion { get => _appVersion; set => SetProperty(ref _appVersion, value); }

    [JsonIgnore] public bool IsOnline => ServerOnline || LanOnline;
    [JsonIgnore] public bool CanSend => IsOnline;
    [JsonIgnore] public string PlatformLabel => Platform.ToLowerInvariant() switch
    {
        "windows" => "Windows",
        "android" => "Android",
        "linux" => "Linux",
        "macos" => "macOS",
        "ios" => "iOS",
        _ => string.IsNullOrWhiteSpace(Platform) ? "设备" : Platform
    };
    [JsonIgnore] public string PlatformGlyph => Platform.ToLowerInvariant() switch
    {
        "windows" => "\uE7F8",
        "android" => "\uE8EA",
        _ => "\uE7F4"
    };
    [JsonIgnore] public string Initial =>
        string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();
    [JsonIgnore] public string ModelLabel =>
        string.IsNullOrWhiteSpace(Model) ? PlatformLabel : Model;
    [JsonIgnore] public string StatusLabel => (ServerOnline, LanOnline) switch
    {
        (true, true) => "局域网 · 服务器在线",
        (true, false) => "服务器在线",
        (false, true) => "局域网在线",
        _ => "离线"
    };
    [JsonIgnore] public string PreferredRoute => LanOnline ? "lan" : ServerOnline ? "relay" : "offline";
    [JsonIgnore] public string PreferredRouteLabel =>
        LanOnline ? "优先局域网直连" : ServerOnline ? "通过服务器中转" : "不可达";
    [JsonIgnore] public string StatusColor => IsOnline ? "#5EE1B7" : "#7B849E";
    [JsonIgnore] public string SourceLabel => (IsKnownOnServer, LanOnline) switch
    {
        (true, true) => "已绑定设备 · 局域网发现",
        (true, false) => "已绑定设备 · 自动上线",
        (false, true) => "局域网发现",
        _ => "设备"
    };
    [JsonIgnore] public string LanAddressLabel =>
        LanOnline && LanAddresses.Count > 0
            ? $"{LanAddresses[0]}:{LanPort}"
            : "—";
    [JsonIgnore] public string IpFamilyLabel
    {
        get
        {
            if (LanOnline && LanFamilies.Count > 0)
            {
                return string.Join(" / ", LanFamilies.Distinct(StringComparer.OrdinalIgnoreCase));
            }

            return ServerIpFamily ?? "IP 未知";
        }
    }
    [JsonIgnore] public string AddressSummary =>
        LanOnline
            ? string.Join(" · ", LanAddresses.Take(2).Select(x => $"{x}:{LanPort}"))
            : ServerIp ?? "在线上，等待建立通道";
    [JsonIgnore] public string LastSeenLabel
    {
        get
        {
            var last = LanOnline && LanLastSeen > LastSeenAt ? LanLastSeen : LastSeenAt;
            return last == default ? "未知" : Formatters.RelativeTime(last);
        }
    }
    [JsonIgnore] public string ServerPresenceLabel =>
        ServerOnline ? $"服务器在线 · {ServerIpFamily ?? "IP 未知"}" : "服务器离线";
    [JsonIgnore] public string LanPresenceLabel =>
        LanOnline ? $"局域网在线 · {IpFamilyLabel}" : "局域网未发现";
    [JsonIgnore] public string DisplayAddress =>
        LanOnline ? LanAddressLabel : ServerIp ?? "";

    public void ApplyServer(Lumen.Shared.DeviceDto device)
    {
        Id = device.Id;
        Name = device.Name;
        Platform = device.Platform;
        Model = device.Model;
        AppVersion = device.AppVersion;
        ServerOnline = device.Online;
        ServerIp = device.LastIp;
        ServerIpFamily = device.LastIpFamily;
        LastSeenAt = device.LastSeenAt;
        IsKnownOnServer = true;
    }

    public void ApplyLan(Lumen.Shared.DiscoveryPacket packet)
    {
        Id = packet.DeviceId;
        Name = packet.Name;
        Platform = packet.Platform;
        Model = packet.Model;
        LanOnline = true;
        LanPort = packet.Port;
        LanAddresses = packet.Addresses.ToList();
        LanFamilies = packet.Families.ToList();
        LanLastSeen = DateTimeOffset.UtcNow;
        RelayUrl = packet.RelayUrl;
    }

    public void MarkLanExpired()
    {
        LanOnline = false;
        LanAddresses = [];
        LanFamilies = [];
    }

    private void NotifyDerived()
    {
        OnPropertiesChanged(
            nameof(IsOnline),
            nameof(CanSend),
            nameof(PlatformLabel),
            nameof(PlatformGlyph),
            nameof(Initial),
            nameof(ModelLabel),
            nameof(StatusLabel),
            nameof(PreferredRoute),
            nameof(PreferredRouteLabel),
            nameof(StatusColor),
            nameof(SourceLabel),
            nameof(LanAddressLabel),
            nameof(IpFamilyLabel),
            nameof(AddressSummary),
            nameof(LastSeenLabel),
            nameof(ServerPresenceLabel),
            nameof(LanPresenceLabel),
            nameof(DisplayAddress));
    }
}
