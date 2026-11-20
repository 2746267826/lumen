using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lumen.Server.Data;
using Lumen.Server.Services;
using Lumen.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Server.Realtime;

public sealed class RealtimeHub(IServiceScopeFactory scopeFactory, ILogger<RealtimeHub> logger)
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, ConcurrentDictionary<string, ClientConnection>>> _users = new();

    public bool IsOnline(Guid deviceId) =>
        _users.Values.Any(devices =>
            devices.TryGetValue(deviceId, out var connections) && !connections.IsEmpty);

    public async Task RunAsync(
        HttpContext context,
        Guid userId,
        DeviceDto device,
        IReadOnlyList<DeviceDto> userDevices)
    {
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new ClientConnection(Guid.NewGuid().ToString("N"), device.Id, socket);
        var userConnections = _users.GetOrAdd(userId, _ => new());
        var deviceConnections = userConnections.GetOrAdd(device.Id, _ => new());
        deviceConnections[connection.Id] = connection;

        var isFirstConnection = deviceConnections.Count == 1;
        logger.LogInformation(
            "Device {DeviceId} connected over relay ({ConnectionCount} socket(s), user {UserId})",
            device.Id,
            deviceConnections.Count,
            userId);

        try
        {
            await connection.SendAsync(new
            {
                type = WireEvents.Hello,
                serverTime = DateTimeOffset.UtcNow,
                deviceId = device.Id,
                devices = userDevices,
                protocol = LumenProtocol.Version
            }, context.RequestAborted);

            if (isFirstConnection)
            {
                BroadcastPresence(userId, device.Id, true, device);
            }

            await ReceiveLoopAsync(context, userId, device, connection);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            deviceConnections.TryRemove(connection.Id, out _);
            var wentOffline = deviceConnections.IsEmpty;
            if (wentOffline)
            {
                userConnections.TryRemove(device.Id, out _);
                await MarkOfflineAsync(device.Id);
                BroadcastPresence(userId, device.Id, false, device);
            }

            connection.Dispose();
            logger.LogInformation("Device {DeviceId} disconnected from relay", device.Id);
        }
    }

    public async Task<bool> SendToDeviceAsync(
        Guid userId,
        Guid deviceId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        if (!_users.TryGetValue(userId, out var devices) ||
            !devices.TryGetValue(deviceId, out var connections) ||
            connections.IsEmpty)
        {
            return false;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LumenJson.Options);
        var sent = false;
        foreach (var connection in connections.Values)
        {
            if (await connection.SendRawAsync(bytes, cancellationToken))
            {
                sent = true;
            }
        }

        return sent;
    }

    public async Task BroadcastAsync(
        Guid userId,
        Guid? exceptDeviceId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        if (!_users.TryGetValue(userId, out var devices))
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LumenJson.Options);
        foreach (var (deviceId, connections) in devices)
        {
            if (deviceId == exceptDeviceId)
            {
                continue;
            }

            foreach (var connection in connections.Values)
            {
                await connection.SendRawAsync(bytes, cancellationToken);
            }
        }
    }

    private async Task ReceiveLoopAsync(
        HttpContext context,
        Guid userId,
        DeviceDto device,
        ClientConnection connection)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();

        while (!context.RequestAborted.IsCancellationRequested &&
               connection.Socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await connection.Socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.Socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "bye",
                        CancellationToken.None);
                    return;
                }

                if (message.Length + result.Count > 256 * 1024)
                {
                    await connection.SendAsync(new
                    {
                        type = WireEvents.Error,
                        code = "message_too_large",
                        message = "WebSocket 控制消息过大。"
                    }, context.RequestAborted);
                    return;
                }

                await message.WriteAsync(buffer.AsMemory(0, result.Count), context.RequestAborted);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            await HandleMessageAsync(context, userId, device, connection, message.ToArray());
        }
    }

    private async Task HandleMessageAsync(
        HttpContext context,
        Guid userId,
        DeviceDto device,
        ClientConnection connection,
        byte[] payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            await connection.SendAsync(new
            {
                type = WireEvents.Error,
                code = "invalid_json",
                message = "无效的 JSON 消息。"
            }, context.RequestAborted);
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("type", out var typeProperty))
            {
                return;
            }

            var type = typeProperty.GetString();
            if (type == "ping")
            {
                long? ts = null;
                if (document.RootElement.TryGetProperty("ts", out var tsProperty) &&
                    tsProperty.TryGetInt64(out var parsedTs))
                {
                    ts = parsedTs;
                }

                await connection.SendAsync(new
                {
                    type = WireEvents.Pong,
                    ts,
                    serverTime = DateTimeOffset.UtcNow
                }, context.RequestAborted);
                await MarkSeenAsync(userId, device.Id, context);
            }
        }
    }

    private void BroadcastPresence(Guid userId, Guid deviceId, bool online, DeviceDto device)
    {
        _ = BroadcastAsync(userId, deviceId, new
        {
            type = online ? WireEvents.DeviceOnline : WireEvents.DeviceOffline,
            device = online ? device with { Online = true } : null,
            deviceId,
            online,
            at = DateTimeOffset.UtcNow
        });
    }

    private async Task MarkSeenAsync(Guid userId, Guid deviceId, HttpContext context)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
            var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.UserId == userId);
            if (device is not null)
            {
                device.LastSeenAt = DateTimeOffset.UtcNow;
                var ip = StorageService.NormalizeRemoteIp(context.Connection.RemoteIpAddress);
                if (!string.IsNullOrEmpty(ip))
                {
                    device.LastIp = ip;
                    device.LastIpFamily = StorageService.GetFamily(ip);
                }

                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to update last-seen for device {DeviceId}", deviceId);
        }
    }

    private async Task MarkOfflineAsync(Guid deviceId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
            var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId);
            if (device is not null)
            {
                device.LastSeenAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to persist offline state for device {DeviceId}", deviceId);
        }
    }

    private sealed class ClientConnection(string id, Guid deviceId, WebSocket socket)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string Id { get; } = id;
        public Guid DeviceId { get; } = deviceId;
        public WebSocket Socket { get; } = socket;

        public Task<bool> SendAsync(object payload, CancellationToken ct) =>
            SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(payload, LumenJson.Options), ct);

        public async Task<bool> SendRawAsync(byte[] bytes, CancellationToken ct)
        {
            if (Socket.State != WebSocketState.Open)
            {
                return false;
            }

            await _sendLock.WaitAsync(ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
                return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            try
            {
                Socket.Dispose();
            }
            catch
            {
            }

            _sendLock.Dispose();
        }
    }
}
