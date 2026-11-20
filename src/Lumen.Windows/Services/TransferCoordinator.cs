using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumen.Shared;
using Lumen.Windows.Infrastructure;
using Lumen.Windows.Models;

namespace Lumen.Windows.Services;

public sealed record CoordinatorNotification(string Message, bool IsError = false);

public sealed class TransferCoordinator : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryStore _history;
    private readonly ApiClient _api;
    private readonly RelayConnection _relay;
    private readonly DirectPeerClient _directClient;
    private readonly DirectTransferServer _directServer;
    private readonly ConcurrentDictionary<Guid, LocalTransferRecord> _records = new();
    private readonly ConcurrentDictionary<Guid, DirectSession> _directSessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _transferCancellations = new();
    private readonly SemaphoreSlim _relayEventLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private int _activeSends;

    public TransferCoordinator(
        AppSettings settings,
        SettingsStore settingsStore,
        HistoryStore history,
        ApiClient api,
        RelayConnection relay,
        DirectPeerClient directClient,
        DirectTransferServer directServer)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _history = history;
        _api = api;
        _relay = relay;
        _directClient = directClient;
        _directServer = directServer;
        _directServer.OfferReceived += (_, record) => IncomingInvite?.Invoke(this, record);
    }

    public event EventHandler<LocalTransferRecord>? IncomingInvite;
    public event EventHandler<CoordinatorNotification>? Notification;
    public event EventHandler? ActivityChanged;

    public bool IsSending => Volatile.Read(ref _activeSends) > 0;

    public async Task SendFilesAsync(
        PeerDevice peer,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            await SendFileAsync(peer, path, ct);
        }
    }

    public Task SendTextAsync(PeerDevice peer, string text, CancellationToken ct = default) =>
        SendPayloadAsync(peer, TransferKinds.Text, "文字消息.txt", "text/plain; charset=utf-8",
            Encoding.UTF8.GetByteCount(text), text, null, ct);

    public async Task AcceptInviteAsync(LocalTransferRecord record, CancellationToken ct = default)
    {
        if (record.IsLan)
        {
            await _directServer.AcceptOfferAsync(record.Id, ct);
            return;
        }

        var dto = await _api.AcceptTransferAsync(record.Id, ct);
        ApplyDto(record, dto, updateProgress: true);
        AddLocalEvent(record, "accepted", "本机已接受服务器中转传输。");
        _history.SaveSoon();
        _ = DownloadRelayAsync(record, _disposeCts.Token);
    }

    public async Task DeclineInviteAsync(LocalTransferRecord record, CancellationToken ct = default)
    {
        if (record.IsLan)
        {
            await _directServer.DeclineOfferAsync(record.Id, ct);
            return;
        }

        var dto = await _api.DeclineTransferAsync(record.Id, ct);
        ApplyDto(record, dto, updateProgress: true);
        _history.SaveSoon();
    }

    public async Task CancelAsync(LocalTransferRecord record, CancellationToken ct = default)
    {
        if (_transferCancellations.TryGetValue(record.Id, out var cts))
        {
            cts.Cancel();
        }

        if (record.IsLan)
        {
            if (_directSessions.TryRemove(record.Id, out var session))
            {
                await _directClient.CancelAsync(session.Endpoint, record.Id, session.UploadToken, ct);
            }

            return;
        }

        var dto = await _api.CancelTransferAsync(record.Id, ct);
        ApplyDto(record, dto, updateProgress: true);
        _history.SaveSoon();
    }

    public async Task HandleRelayEventAsync(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeProperty))
        {
            return;
        }

        var type = typeProperty.GetString();
        await _relayEventLock.WaitAsync();
        try
        {
            switch (type)
            {
                case WireEvents.TransferCreated:
                    await HandleRelayCreatedAsync(root);
                    break;
                case WireEvents.TransferAccepted:
                case WireEvents.TransferReady:
                case WireEvents.TransferCompleted:
                    await HandleRelayDtoEventAsync(type, root);
                    break;
                case WireEvents.TransferDeclined:
                case WireEvents.TransferCancelled:
                    await HandleRelayTerminalEventAsync(type, root);
                    break;
                case WireEvents.TransferProgress:
                    await HandleRelayProgressAsync(root);
                    break;
                case WireEvents.TransferFailed:
                    await HandleRelayFailedAsync(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            Notify("处理服务器事件失败：" + ex.Message, true);
        }
        finally
        {
            _relayEventLock.Release();
        }
    }

    private async Task SendFileAsync(PeerDevice peer, string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            Notify("文件不存在：" + path, true);
            return;
        }

        if (!peer.CanSend)
        {
            Notify("对方设备当前不可达。", true);
            return;
        }

        var mime = FileUtilities.GetMimeType(path);
        var kind = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? TransferKinds.Image
            : TransferKinds.File;
        await SendPayloadAsync(
            peer,
            kind,
            info.Name,
            mime,
            info.Length,
            null,
            path,
            ct);
    }

    private async Task SendPayloadAsync(
        PeerDevice peer,
        string kind,
        string name,
        string mimeType,
        long size,
        string? text,
        string? path,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _activeSends);
        ActivityChanged?.Invoke(this, EventArgs.Empty);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var transferIdHint = Guid.NewGuid();
        _transferCancellations[transferIdHint] = linkedCts;
        try
        {
            var route = await ChooseRouteAsync(peer, kind, name, size, linkedCts.Token);
            var sha = kind == TransferKinds.Text
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant()
                : path is not null
                    ? await FileUtilities.Sha256FileAsync(path, linkedCts.Token)
                    : null;

            if (route.Endpoint is not null)
            {
                try
                {
                    await SendDirectAsync(
                        peer,
                        route.Endpoint,
                        kind,
                        name,
                        mimeType,
                        size,
                        sha,
                        text,
                        path,
                        linkedCts.Token);
                    return;
                }
                catch (DirectOfferFailedException ex) when (peer.ServerOnline)
                {
                    Notify($"局域网直连失败，自动切换服务器中转：{ex.Message}");
                    route = RouteDecision.Relay("局域网直连不可用，已切换服务器中转。");
                }
                catch (DirectTransferFailedException ex) when (peer.ServerOnline)
                {
                    Notify($"局域网传输中断，自动切换服务器中转：{ex.Message}");
                    route = RouteDecision.Relay("局域网传输中断，已切换服务器中转。");
                }
            }

            if (peer.ServerOnline)
            {
                await SendRelayAsync(
                    peer,
                    route,
                    kind,
                    name,
                    mimeType,
                    size,
                    sha,
                    text,
                    path,
                    linkedCts.Token);
                return;
            }

            throw new InvalidOperationException(
                "局域网直连不可达，且服务器上的接收设备不在线。");
        }
        catch (OperationCanceledException)
        {
            Notify("传输已取消。");
        }
        catch (Exception ex)
        {
            Notify("发送失败：" + ex.Message, true);
        }
        finally
        {
            _transferCancellations.TryRemove(transferIdHint, out _);
            linkedCts.Dispose();
            Interlocked.Decrement(ref _activeSends);
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<RouteDecision> ChooseRouteAsync(
        PeerDevice peer,
        string kind,
        string name,
        long size,
        CancellationToken ct)
    {
        if (peer.LanOnline)
        {
            var endpoint = await _directClient.ProbeAsync(peer, ct);
            if (endpoint is not null)
            {
                Notify($"已选择局域网直连：{endpoint.Address} · {endpoint.Family}");
                return RouteDecision.Direct(endpoint, $"局域网直连 · {endpoint.Family}");
            }
        }

        if (peer.ServerOnline)
        {
            return RouteDecision.Relay("局域网不可达，使用服务器中转。");
        }

        throw new InvalidOperationException("没有可用传输通道。");
    }

    private async Task SendDirectAsync(
        PeerDevice peer,
        DirectEndpoint endpoint,
        string kind,
        string name,
        string mimeType,
        long size,
        string? sha,
        string? text,
        string? path,
        CancellationToken ct)
    {
        var chunkSize = kind == TransferKinds.Text
            ? 0
            : Math.Clamp(_settings.ChunkSize, 64 * 1024, LumenProtocol.MaxChunkSize);
        var offer = new DirectOfferRequest(
            _settings.DeviceId,
            _settings.DeviceName,
            "windows",
            kind,
            name,
            mimeType,
            size,
            sha,
            chunkSize,
            text,
            DateTimeOffset.UtcNow);
        DirectOfferResponse response;
        try
        {
            response = await _directClient.SendOfferAsync(endpoint, offer, ct);
        }
        catch (Exception ex)
        {
            throw new DirectOfferFailedException(ex.Message, ex);
        }

        var totalChunks = kind == TransferKinds.Text || size == 0
            ? 0
            : (int)Math.Min(int.MaxValue, (size + chunkSize - 1) / chunkSize);
        var localAddress = NetworkUtilities.GetLocalAddresses()
            .FirstOrDefault(x => x.Family == endpoint.Family)?.Address
            ?? NetworkUtilities.GetLocalAddresses().FirstOrDefault()?.Address
            ?? "";
        var record = new LocalTransferRecord
        {
            Id = response.TransferId,
            Direction = "out",
            Route = TransferRoutes.Lan,
            Status = TransferStatuses.Pending,
            Kind = kind,
            Name = name,
            MimeType = mimeType,
            Size = size,
            Sha256 = sha,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks,
            PeerName = peer.Name,
            PeerDeviceId = peer.Id.ToString(),
            PeerPlatform = peer.Platform,
            LocalAddress = localAddress,
            RemoteAddress = endpoint.Address,
            IpFamily = endpoint.Family,
            LocalPath = path,
            TextContent = text,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = "created",
            Message = "已发送局域网传输邀请，等待对方确认。",
            Address = endpoint.Address,
            IpFamily = endpoint.Family
        });
        record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = "route.selected",
            Message = $"局域网直连 · {endpoint.Family}，不经过服务器。",
            Address = endpoint.Address,
            IpFamily = endpoint.Family
        });
        RegisterRecord(record);
        _directSessions[record.Id] = new DirectSession(endpoint, response.UploadToken);

        bool accepted;
        try
        {
            accepted = await WaitDirectAcceptedAsync(endpoint, record, response.UploadToken, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DirectTransferFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = "局域网等待确认失败：" + ex.Message;
            MarkDirectFailed(record, message);
            throw new DirectTransferFailedException(message, ex);
        }

        if (!accepted)
        {
            return;
        }

        try
        {
            if (kind == TransferKinds.Text)
            {
                await _directClient.CompleteAsync(endpoint, record.Id, response.UploadToken, ct);
                record.Status = TransferStatuses.Completed;
                record.TransferredBytes = size;
                record.CompletedAt = DateTimeOffset.UtcNow;
                record.UpdatedAt = record.CompletedAt.Value;
                AddLocalEvent(record, "completed", "文字已通过局域网直连送达。");
                _history.SaveSoon();
                Notify($"“{name}”已送达 {peer.Name}（局域网）。");
                return;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                await UploadDirectFileAsync(
                    endpoint,
                    record,
                    path,
                    response.UploadToken,
                    ct);
            }

            await _directClient.CompleteAsync(endpoint, record.Id, response.UploadToken, ct);
            record.Status = TransferStatuses.Completed;
            record.TransferredBytes = size;
            record.SpeedBps = 0;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.UpdatedAt = record.CompletedAt.Value;
            AddLocalEvent(record, "completed", "接收端已合并文件并通过 SHA-256 校验。");
            _history.SaveSoon();
            Notify($"“{name}”已送达 {peer.Name}（局域网）。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DirectTransferFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = "局域网传输中断：" + ex.Message;
            MarkDirectFailed(record, message);
            throw new DirectTransferFailedException(message, ex);
        }
    }

    private async Task<bool> WaitDirectAcceptedAsync(
        DirectEndpoint endpoint,
        LocalTransferRecord record,
        string uploadToken,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await _directClient.GetOfferStatusAsync(endpoint, record.Id, ct);
            if (status.Status is TransferStatuses.Accepted or TransferStatuses.Uploading)
            {
                record.Status = status.Status;
                record.UpdatedAt = status.UpdatedAt;
                AddLocalEvent(record, "accepted", "对方已接受局域网传输。");
                _history.SaveSoon();
                return true;
            }

            if (status.Status is TransferStatuses.Declined or TransferStatuses.Cancelled)
            {
                record.Status = status.Status;
                record.UpdatedAt = DateTimeOffset.UtcNow;
                AddLocalEvent(record, status.Status, "接收设备拒绝或取消了传输。");
                _history.SaveSoon();
                Notify("对方拒绝或取消了局域网传输。", status.Status == TransferStatuses.Declined);
                return false;
            }

            await Task.Delay(600, ct);
        }

        const string timeoutMessage = "等待接收方确认超时。";
        MarkDirectFailed(record, timeoutMessage);
        try
        {
            await _directClient.CancelAsync(endpoint, record.Id, uploadToken, ct);
        }
        catch
        {
        }
        throw new DirectTransferFailedException(timeoutMessage);
    }

    private void MarkDirectFailed(LocalTransferRecord record, string message)
    {
        record.Status = TransferStatuses.Failed;
        record.Error = message;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        AddLocalEvent(record, "failed", message);
        _history.SaveSoon();
    }

    private async Task UploadDirectFileAsync(
        DirectEndpoint endpoint,
        LocalTransferRecord record,
        string path,
        string uploadToken,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            record.ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[record.ChunkSize];
        record.Status = TransferStatuses.Uploading;
        var overall = Stopwatch.StartNew();
        long sent = 0;
        for (var index = 0; index < record.TotalChunks; index++)
        {
            ct.ThrowIfCancellationRequested();
            var expected = (int)Math.Min(record.ChunkSize, record.Size - sent);
            var read = await ReadExactAsync(stream, buffer.AsMemory(0, expected), ct);
            if (read != expected)
            {
                throw new IOException("读取本地文件时意外结束。");
            }

            var data = buffer.AsSpan(0, expected).ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            var stopwatch = Stopwatch.StartNew();
            await RetryAsync(
                token => _directClient.UploadChunkAsync(
                    endpoint,
                    record.Id,
                    index,
                    uploadToken,
                    data,
                    sha,
                    token),
                3,
                ct);
            stopwatch.Stop();
            sent += expected;
            var speed = expected / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            record.TransferredBytes = sent;
            record.CompletedChunks = index + 1;
            record.SpeedBps = record.SpeedBps > 0
                ? record.SpeedBps * 0.7 + speed * 0.3
                : speed;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            record.AddEvent(new TransferEventRecord
            {
                At = DateTimeOffset.UtcNow,
                Type = "chunk.uploaded",
                Message = $"已发送分块 {index + 1}/{record.TotalChunks}。",
                Bytes = expected,
                SpeedBps = speed,
                Address = endpoint.Address,
                IpFamily = endpoint.Family
            });
            _history.SaveSoon();
        }

        record.SpeedBps = sent / Math.Max(0.001, overall.Elapsed.TotalSeconds);
        record.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task SendRelayAsync(
        PeerDevice peer,
        RouteDecision route,
        string kind,
        string name,
        string mimeType,
        long size,
        string? sha,
        string? text,
        string? path,
        CancellationToken ct)
    {
        var chunkSize = kind == TransferKinds.Text
            ? 0
            : Math.Clamp(_settings.ChunkSize, 64 * 1024, LumenProtocol.MaxChunkSize);
        var dto = await _api.CreateTransferAsync(
            new TransferCreateRequest(
                peer.Id,
                kind,
                name,
                mimeType,
                size,
                sha,
                chunkSize,
                text),
            ct);
        var record = new LocalTransferRecord
        {
            Id = dto.Id,
            Direction = "out",
            Route = TransferRoutes.Relay,
            Status = dto.Status,
            Kind = dto.Kind,
            Name = dto.Name,
            MimeType = dto.MimeType,
            Size = dto.Size,
            Sha256 = dto.Sha256,
            ChunkSize = dto.ChunkSize,
            TotalChunks = dto.TotalChunks,
            PeerName = peer.Name,
            PeerDeviceId = peer.Id.ToString(),
            PeerPlatform = peer.Platform,
            LocalAddress = dto.SenderAddress,
            RemoteAddress = dto.RecipientAddress,
            ServerAddress = new Uri(ApiClient.NormalizeServerUrl(_settings.ServerUrl)).Authority,
            IpFamily = NetworkUtilities.FamilyOf(dto.RecipientAddress),
            LocalPath = path,
            TextContent = text,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt
        };
        record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = "route.selected",
            Message = route.Reason,
            Address = dto.SenderAddress,
            IpFamily = record.IpFamily
        });
        record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = "created",
            Message = "传输邀请已通过服务器发送，等待对方确认。",
            Address = dto.SenderAddress,
            IpFamily = NetworkUtilities.FamilyOf(dto.SenderAddress)
        });
        RegisterRecord(record);

        var accepted = await WaitRelayAcceptedAsync(record, ct);
        if (!accepted)
        {
            return;
        }

        if (kind != TransferKinds.Text)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("缺少待发送文件。");
            }

            await UploadRelayFileAsync(record, path, ct);
        }

        var ready = await _api.ReadyTransferAsync(record.Id, ct);
        ApplyDto(record, ready, updateProgress: true);
        AddLocalEvent(record, "ready", "服务端已收齐内容，等待接收方下载。");
        _history.SaveSoon();
        Notify($"“{name}”已上传到服务器，等待 {peer.Name} 接收。");
    }

    private async Task<bool> WaitRelayAcceptedAsync(
        LocalTransferRecord record,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var details = await _api.GetTransferAsync(record.Id, ct);
            ApplyDto(record, details.Transfer, updateProgress: true);
            if (record.Status is TransferStatuses.Accepted or TransferStatuses.Uploading)
            {
                AddLocalEvent(record, "accepted", "接收设备已接受服务器中转传输。");
                _history.SaveSoon();
                return true;
            }

            if (record.Status is TransferStatuses.Declined or TransferStatuses.Cancelled)
            {
                Notify("对方拒绝或取消了传输。", true);
                return false;
            }

            await Task.Delay(650, ct);
        }

        record.Status = TransferStatuses.Failed;
        record.Error = "等待接收方确认超时。";
        record.UpdatedAt = DateTimeOffset.UtcNow;
        AddLocalEvent(record, "failed", record.Error);
        _history.SaveSoon();
        try
        {
            await _api.CancelTransferAsync(record.Id, ct);
        }
        catch
        {
        }

        throw new TimeoutException(record.Error);
    }

    private async Task UploadRelayFileAsync(
        LocalTransferRecord record,
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            record.ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[record.ChunkSize];
        var overall = Stopwatch.StartNew();
        long sent = 0;
        record.Status = TransferStatuses.Uploading;
        for (var index = 0; index < record.TotalChunks; index++)
        {
            ct.ThrowIfCancellationRequested();
            var expected = (int)Math.Min(record.ChunkSize, record.Size - sent);
            var read = await ReadExactAsync(stream, buffer.AsMemory(0, expected), ct);
            if (read != expected)
            {
                throw new IOException("读取本地文件时意外结束。");
            }

            var data = buffer.AsSpan(0, expected).ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            var started = Stopwatch.StartNew();
            var result = await RetryAsync(
                token => _api.UploadChunkAsync(record.Id, index, data, sha, token),
                3,
                ct);
            started.Stop();
            sent += expected;
            var speed = expected / Math.Max(0.001, started.Elapsed.TotalSeconds);
            record.TransferredBytes = result.TransferredBytes;
            record.CompletedChunks = result.CompletedChunks;
            record.SpeedBps = record.SpeedBps > 0
                ? record.SpeedBps * 0.7 + speed * 0.3
                : speed;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            record.AddEvent(new TransferEventRecord
            {
                At = DateTimeOffset.UtcNow,
                Type = "chunk.uploaded",
                Message = $"服务端已接收分块 {result.CompletedChunks}/{result.TotalChunks}。",
                Bytes = expected,
                SpeedBps = speed,
                Address = record.ServerAddress,
                IpFamily = record.IpFamily
            });
            _history.SaveSoon();
        }

        record.SpeedBps = sent / Math.Max(0.001, overall.Elapsed.TotalSeconds);
    }

    private async Task DownloadRelayAsync(LocalTransferRecord record, CancellationToken ct)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var details = await _api.GetTransferAsync(record.Id, ct);
                ApplyDto(record, details.Transfer, updateProgress: true);
                if (record.Status == TransferStatuses.Ready)
                {
                    break;
                }

                if (record.Status is TransferStatuses.Declined or
                    TransferStatuses.Cancelled or
                    TransferStatuses.Failed or
                    TransferStatuses.Completed)
                {
                    return;
                }

                await Task.Delay(700, ct);
            }

            if (record.Status != TransferStatuses.Ready)
            {
                throw new TimeoutException("等待发送端上传超时。");
            }

            var finalDetails = await _api.GetTransferAsync(record.Id, ct);
            var final = finalDetails.Transfer;
            if (record.Kind == TransferKinds.Text)
            {
                record.TextContent = final.TextContent;
                record.TransferredBytes = record.Size;
                var completed = await _api.ReceivedTransferAsync(record.Id, ct);
                ApplyDto(record, completed, updateProgress: true);
                record.CompletedAt = completed.CompletedAt ?? DateTimeOffset.UtcNow;
                AddLocalEvent(record, "completed", "文字消息已接收。");
                _history.SaveSoon();
                Notify($"已收到来自 {record.PeerName} 的文字。");
                return;
            }

            var tempDirectory = Path.Combine(
                _settingsStore.DataDirectory,
                "incoming",
                record.Id.ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var tempFile = Path.Combine(tempDirectory, "download.part");
            var overall = Stopwatch.StartNew();
            long downloaded = 0;
            string? actualSha;
            await using (var output = new FileStream(
                tempFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                for (var index = 0; index < record.TotalChunks; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var indexCopy = index;
                    using var chunk = new MemoryStream();
                    var result = await RetryAsync(
                        token => _api.DownloadChunkAsync(record.Id, indexCopy, chunk, token),
                        4,
                        ct);
                    chunk.Position = 0;
                    var expected = (int)Math.Min(
                        record.ChunkSize,
                        record.Size - downloaded);
                    if (result.Size > 0 && result.Size != expected)
                    {
                        throw new IOException($"分块 {index} 长度异常。");
                    }

                    var chunkBytes = chunk.GetBuffer().AsSpan(0, (int)chunk.Length);
                    if (!string.IsNullOrWhiteSpace(result.Sha256))
                    {
                        var sha = Convert.ToHexString(SHA256.HashData(chunkBytes)).ToLowerInvariant();
                        if (!sha.Equals(result.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException($"分块 {index} SHA-256 校验失败。");
                        }
                    }

                    hasher.AppendData(chunkBytes);
                    await output.WriteAsync(chunkBytes.ToArray(), ct);
                    downloaded += chunk.Length;
                    record.TransferredBytes = downloaded;
                    record.CompletedChunks = index + 1;
                    var elapsed = Math.Max(0.001, overall.Elapsed.TotalSeconds);
                    record.SpeedBps = downloaded / elapsed;
                    record.Status = TransferStatuses.Uploading;
                    record.UpdatedAt = DateTimeOffset.UtcNow;
                    AddLocalEvent(
                        record,
                        "chunk.downloaded",
                        $"已下载分块 {index + 1}/{record.TotalChunks}。",
                        bytes: chunk.Length,
                        speed: record.SpeedBps,
                        quiet: true);
                    _history.SaveSoon();
                }

                await output.FlushAsync(ct);
                actualSha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(record.Sha256) &&
                !actualSha.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("文件整体 SHA-256 校验失败。");
            }

            var finalPath = FileUtilities.ResolveIncomingPath(
                _settings.ReceiveDirectory,
                record.Name);
            if (record.Size == 0)
            {
                await File.WriteAllBytesAsync(tempFile, [], ct);
            }

            File.Move(tempFile, finalPath, overwrite: false);
            record.LocalPath = finalPath;
            record.Sha256 = actualSha;
            record.TransferredBytes = record.Size;
            record.CompletedChunks = record.TotalChunks;
            record.Status = TransferStatuses.Ready;
            record.SpeedBps = record.Size / Math.Max(0.001, overall.Elapsed.TotalSeconds);
            AddLocalEvent(record, "merged", "全部分块已下载并合并，SHA-256 校验通过。");
            var finalDto = await _api.ReceivedTransferAsync(record.Id, ct);
            ApplyDto(record, finalDto, updateProgress: true);
            record.CompletedAt = finalDto.CompletedAt ?? DateTimeOffset.UtcNow;
            record.LocalPath = finalPath;
            record.SpeedBps = record.Size / Math.Max(0.001, overall.Elapsed.TotalSeconds);
            AddLocalEvent(record, "completed", "文件已保存到接收目录。");
            _history.SaveSoon();
            Notify($"已收到来自 {record.PeerName} 的文件：{record.Name}");
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            record.Status = TransferStatuses.Failed;
            record.Error = ex.Message;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            AddLocalEvent(record, "failed", "接收失败：" + ex.Message);
            _history.SaveSoon();
            Notify("接收失败：" + ex.Message, true);
        }
    }

    private async Task HandleRelayCreatedAsync(JsonElement root)
    {
        if (!root.TryGetProperty("transfer", out var transferElement))
        {
            return;
        }

        var dto = transferElement.Deserialize<TransferDto>(LumenJson.Options);
        if (dto is null ||
            dto.RecipientDeviceId != _settings.DeviceId ||
            dto.SenderDeviceId == _settings.DeviceId)
        {
            return;
        }

        if (_records.ContainsKey(dto.Id))
        {
            return;
        }

        var record = new LocalTransferRecord
        {
            Id = dto.Id,
            Direction = "in",
            Route = TransferRoutes.Relay,
            Status = dto.Status,
            Kind = dto.Kind,
            Name = dto.Name,
            MimeType = dto.MimeType,
            Size = dto.Size,
            Sha256 = dto.Sha256,
            ChunkSize = dto.ChunkSize,
            TotalChunks = dto.TotalChunks,
            PeerName = dto.SenderDeviceName,
            PeerDeviceId = dto.SenderDeviceId.ToString(),
            PeerPlatform = "windows",
            LocalAddress = dto.RecipientAddress,
            RemoteAddress = dto.SenderAddress,
            ServerAddress = new Uri(ApiClient.NormalizeServerUrl(_settings.ServerUrl)).Authority,
            IpFamily = NetworkUtilities.FamilyOf(dto.SenderAddress),
            TextContent = dto.TextContent,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt
        };
        record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = "created",
            Message = "收到服务器中转传输邀请。",
            Address = dto.SenderAddress,
            IpFamily = record.IpFamily
        });
        record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = "route.selected",
            Message = "服务器中转，服务器将暂存加密传输内容。",
            Address = record.ServerAddress,
            IpFamily = record.IpFamily
        });
        RegisterRecord(record);
        Notify($"收到来自 {record.PeerName} 的传输邀请。");
        IncomingInvite?.Invoke(this, record);
    }

    private Task HandleRelayDtoEventAsync(string? type, JsonElement root)
    {
        if (!root.TryGetProperty("transfer", out var transferElement))
        {
            return Task.CompletedTask;
        }

        var dto = transferElement.Deserialize<TransferDto>(LumenJson.Options);
        if (dto is null)
        {
            return Task.CompletedTask;
        }

        var record = GetRecord(dto.Id);
        if (record is null)
        {
            return Task.CompletedTask;
        }

        ApplyDto(record, dto, updateProgress: true);
        if (type == WireEvents.TransferCompleted)
        {
            record.TransferredBytes = record.Size;
            record.CompletedAt = dto.CompletedAt ?? DateTimeOffset.UtcNow;
            record.SpeedBps = 0;
            AddLocalEvent(record, "completed", "接收设备已确认完整接收。");
            Notify($"“{record.Name}”已完成传输。");
        }
        else if (type == WireEvents.TransferReady)
        {
            AddLocalEvent(record, "ready", "服务端内容已就绪。");
        }

        _history.SaveSoon();
        return Task.CompletedTask;
    }

    private Task HandleRelayProgressAsync(JsonElement root)
    {
        if (!root.TryGetProperty("transfer", out var transferElement))
        {
            return Task.CompletedTask;
        }

        var dto = transferElement.Deserialize<TransferDto>(LumenJson.Options);
        if (dto is null)
        {
            return Task.CompletedTask;
        }

        var record = GetRecord(dto.Id);
        if (record is null)
        {
            return Task.CompletedTask;
        }

        if (record.IsOutgoing)
        {
            ApplyDto(record, dto, updateProgress: true);
        }
        else
        {
            record.Status = dto.Status;
            record.CompletedChunks = dto.CompletedChunks;
            record.UpdatedAt = dto.UpdatedAt;
        }

        if (root.TryGetProperty("event", out var eventElement))
        {
            var serverEvent = eventElement.Deserialize<TransferEventDto>(LumenJson.Options);
            if (serverEvent is not null && serverEvent.Type != "chunk.uploaded")
            {
                AddLocalEvent(record, serverEvent.Type, serverEvent.Message, serverEvent.Bytes,
                    serverEvent.SpeedBps, serverEvent.Address, quiet: true);
            }
        }

        _history.SaveSoon();
        return Task.CompletedTask;
    }

    private Task HandleRelayTerminalEventAsync(string? type, JsonElement root)
    {
        if (!root.TryGetProperty("transferId", out var idProperty) ||
            !idProperty.TryGetGuid(out var id))
        {
            return Task.CompletedTask;
        }

        var record = GetRecord(id);
        if (record is null)
        {
            return Task.CompletedTask;
        }

        record.Status = type == WireEvents.TransferDeclined
            ? TransferStatuses.Declined
            : TransferStatuses.Cancelled;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        AddLocalEvent(record, record.Status, record.Status == TransferStatuses.Declined
            ? "接收设备拒绝了传输。"
            : "传输已取消。");
        _history.SaveSoon();
        return Task.CompletedTask;
    }

    private Task HandleRelayFailedAsync(JsonElement root)
    {
        if (root.TryGetProperty("transferId", out var idProperty) &&
            idProperty.TryGetGuid(out var id))
        {
            var record = GetRecord(id);
            if (record is not null)
            {
                record.Status = TransferStatuses.Failed;
                record.Error = root.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : "服务器报告传输失败。";
                record.UpdatedAt = DateTimeOffset.UtcNow;
                AddLocalEvent(record, "failed", record.Error);
                _history.SaveSoon();
            }
        }

        return Task.CompletedTask;
    }

    private void ApplyDto(
        LocalTransferRecord record,
        TransferDto dto,
        bool updateProgress)
    {
        record.Status = dto.Status;
        record.Size = dto.Size;
        record.ChunkSize = dto.ChunkSize;
        record.TotalChunks = dto.TotalChunks;
        record.Sha256 = dto.Sha256;
        record.Name = dto.Name;
        record.MimeType = dto.MimeType;
        record.TextContent = dto.TextContent ?? record.TextContent;
        if (updateProgress)
        {
            record.CompletedChunks = dto.CompletedChunks;
            record.TransferredBytes = dto.TransferredBytes;
            record.SpeedBps = dto.AverageSpeedBps ?? record.SpeedBps;
        }

        record.LocalAddress = record.IsIncoming ? dto.RecipientAddress : dto.SenderAddress;
        record.RemoteAddress = record.IsIncoming ? dto.SenderAddress : dto.RecipientAddress;
        record.IpFamily = NetworkUtilities.FamilyOf(record.RemoteAddress);
        record.UpdatedAt = dto.UpdatedAt;
        if (dto.CompletedAt is { } completed)
        {
            record.CompletedAt = completed;
        }
    }

    private void RegisterRecord(LocalTransferRecord record)
    {
        _records[record.Id] = record;
        _history.Add(record);
    }

    private LocalTransferRecord? GetRecord(Guid id) =>
        _records.TryGetValue(id, out var record) ? record : null;

    private void AddLocalEvent(
        LocalTransferRecord record,
        string type,
        string message,
        long? bytes = null,
        double? speed = null,
        string? address = null,
        bool quiet = false)
    {
        if (quiet && type == "chunk.downloaded")
        {
            var last = record.Events.LastOrDefault(x => x.Type == "chunk.downloaded");
            if (last is not null &&
                record.CompletedChunks % 8 != 0 &&
                record.CompletedChunks != record.TotalChunks)
            {
                return;
            }
        }

        record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = type,
            Message = message,
            Bytes = bytes,
            SpeedBps = speed,
            Address = address ?? (record.IsLan ? record.RemoteAddress : record.ServerAddress),
            IpFamily = record.IpFamily
        });
    }

    private void Notify(string message, bool isError = false) =>
        Notification?.Invoke(this, new CoordinatorNotification(message, isError));

    private static async Task<int> ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> action,
        int attempts,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                attempt < attempts &&
                ex is HttpRequestException or IOException or TaskCanceledException ||
                ex is ApiClientException { StatusCode: >= 500 })
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct);
            }
        }

        throw last ?? new InvalidOperationException("重试失败。");
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        foreach (var cts in _transferCancellations.Values)
        {
            cts.Cancel();
        }

        _disposeCts.Dispose();
        _relayEventLock.Dispose();
        await Task.CompletedTask;
    }

    private sealed record DirectSession(DirectEndpoint Endpoint, string UploadToken);

    private sealed record RouteDecision(DirectEndpoint? Endpoint, string Reason)
    {
        public static RouteDecision Direct(DirectEndpoint endpoint, string reason) =>
            new(endpoint, reason);

        public static RouteDecision Relay(string reason) => new(null, reason);
    }

    private sealed class DirectOfferFailedException(string message, Exception inner)
        : Exception(message, inner);

    private sealed class DirectTransferFailedException(string message, Exception? inner = null)
        : Exception(message, inner);
}
