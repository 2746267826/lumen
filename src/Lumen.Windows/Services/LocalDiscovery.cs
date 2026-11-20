using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Lumen.Shared;
using Lumen.Windows.Models;

namespace Lumen.Windows.Services;

public sealed class LocalDiscovery : IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _listener;
    private UdpClient? _sender;
    private int _directPort;
    private bool _disposed;

    public LocalDiscovery(SettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
    }

    public event EventHandler<DiscoveryPacket>? PeerSeen;
    public event EventHandler<Guid>? PeerLost;
    public event EventHandler? StateChanged;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<LocalAddressInfo> LocalAddresses { get; private set; } = [];
    public DateTimeOffset LastAnnouncement { get; private set; }
    public int SeenPeerCount => _seen.Count;

    public Task StartAsync(int directPort, CancellationToken ct = default)
    {
        _directPort = directPort;
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            RefreshLocalAddresses();
            var listener = new UdpClient(AddressFamily.InterNetwork);
            listener.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, LumenProtocol.DiscoveryPort));
            var group = IPAddress.Parse(LumenProtocol.MulticastGroupV4);
            var joined = false;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    try
                    {
                        listener.JoinMulticastGroup(group, address.Address);
                        joined = true;
                    }
                    catch
                    {
                    }
                }
            }

            if (!joined)
            {
                listener.JoinMulticastGroup(group);
            }

            _listener = listener;
            _sender = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };
            _sender.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            IsRunning = true;
            LastError = null;
            StateChanged?.Invoke(this, EventArgs.Empty);

            _ = ReceiveLoopAsync(_cts.Token);
            _ = AnnounceLoopAsync(_cts.Token);
            _ = ExpiryLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            LastError = $"局域网发现启动失败：{ex.Message}";
            IsRunning = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _sender?.Dispose();
        _listener = null;
        _sender = null;
        IsRunning = false;
        _cts?.Dispose();
        _cts = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener!.ReceiveAsync(ct);
                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(
                    result.Buffer,
                    LumenJson.Options);
                if (packet is null ||
                    packet.Magic != LumenProtocol.DiscoveryMagic ||
                    packet.Version != LumenProtocol.Version ||
                    packet.DeviceId == _settings.DeviceId ||
                    packet.DeviceId == Guid.Empty)
                {
                    continue;
                }

                _seen[packet.DeviceId] = DateTimeOffset.UtcNow;
                PeerSeen?.Invoke(this, packet);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = $"局域网发现接收异常：{ex.Message}";
                StateChanged?.Invoke(this, EventArgs.Empty);
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RefreshLocalAddresses();
                var packet = new DiscoveryPacket(
                    LumenProtocol.DiscoveryMagic,
                    LumenProtocol.Version,
                    _settings.DeviceId,
                    _settings.DeviceName,
                    "windows",
                    Environment.MachineName,
                    _directPort,
                    LocalAddresses.Select(x => x.Address).ToArray(),
                    LocalAddresses.Select(x => x.Family).Distinct().ToArray(),
                    DateTimeOffset.UtcNow,
                    string.IsNullOrWhiteSpace(_settings.AuthToken)
                        ? null
                        : ApiClient.NormalizeServerUrl(_settings.ServerUrl),
                    _settings.UserId);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, LumenJson.Options);
                var groupEndpoint = new IPEndPoint(
                    IPAddress.Parse(LumenProtocol.MulticastGroupV4),
                    LumenProtocol.DiscoveryPort);
                await _sender!.SendAsync(bytes, groupEndpoint, ct);
                try
                {
                    await _sender.SendAsync(
                        bytes,
                        new IPEndPoint(IPAddress.Broadcast, LumenProtocol.DiscoveryPort),
                        ct);
                }
                catch
                {
                }

                LastAnnouncement = DateTimeOffset.UtcNow;
                LastError = null;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = $"局域网广播异常：{ex.Message}";
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ExpiryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-12);
            foreach (var (deviceId, lastSeen) in _seen)
            {
                if (lastSeen < cutoff && _seen.TryRemove(deviceId, out _))
                {
                    PeerLost?.Invoke(this, deviceId);
                }
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshLocalAddresses()
    {
        var next = NetworkUtilities.GetLocalAddresses();
        if (!next.SequenceEqual(LocalAddresses))
        {
            LocalAddresses = next;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _listener?.Dispose();
        _sender?.Dispose();
        _cts?.Dispose();
    }
}
