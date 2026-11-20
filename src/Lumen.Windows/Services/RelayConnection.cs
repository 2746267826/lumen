using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lumen.Shared;
using Lumen.Windows.Models;

namespace Lumen.Windows.Services;

public sealed class RelayConnection : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ClientWebSocket? _socket;
    private bool _disposed;

    public RelayConnection(AppSettings settings)
    {
        _settings = settings;
    }

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<long>? LatencyChanged;
    public event EventHandler<JsonElement>? MessageReceived;

    public bool IsConnected { get; private set; }
    public long LatencyMs { get; private set; }
    public string? LastError { get; private set; }

    public void Start()
    {
        if (!string.IsNullOrWhiteSpace(_settings.AuthToken))
        {
            _ = RestartAsync();
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        if (string.IsNullOrWhiteSpace(_settings.AuthToken))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = RunLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_socket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "restart",
                    CancellationToken.None);
            }
            catch
            {
            }
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _socket?.Dispose();
        _socket = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _socket = socket;
                await socket.ConnectAsync(BuildUri(), ct);
                attempt = 0;
                IsConnected = true;
                LastError = null;
                ConnectionChanged?.Invoke(this, true);
                var pingTask = PingLoopAsync(socket, ct);
                await ReceiveLoopAsync(socket, ct);
                await pingTask;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                if (IsConnected)
                {
                    IsConnected = false;
                    ConnectionChanged?.Invoke(this, false);
                }

                try
                {
                    _socket?.Dispose();
                }
                catch
                {
                }

                _socket = null;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(15, 2 + attempt * 2));
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (message.Length + result.Count > 256 * 1024)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement.Clone();
                if (root.TryGetProperty("type", out var type) &&
                    type.GetString() == WireEvents.Pong)
                {
                    if (root.TryGetProperty("ts", out var ts) &&
                        ts.TryGetInt64(out var sentAt))
                    {
                        LatencyMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentAt);
                        LatencyChanged?.Invoke(this, LatencyMs);
                    }
                }

                MessageReceived?.Invoke(this, root);
            }
            catch (JsonException)
            {
            }
        }
    }

    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                await SendAsync(new
                {
                    type = "ping",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }, ct);
            }
            catch
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    public async Task SendAsync(object payload, CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("服务器通道未连接。");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LumenJson.Options);
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Uri BuildUri()
    {
        var baseUri = new Uri(ApiClient.NormalizeServerUrl(_settings.ServerUrl));
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/api/v1/ws",
            Query =
                $"deviceId={_settings.DeviceId}" +
                $"&access_token={Uri.EscapeDataString(_settings.AuthToken ?? "")}"
        };
        return builder.Uri;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _socket?.Dispose();
        _sendLock.Dispose();
    }
}
