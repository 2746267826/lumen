using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumen.Shared;
using Lumen.Windows.Infrastructure;
using Lumen.Windows.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumen.Windows.Services;

public sealed class DirectTransferServer : IAsyncDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly ConcurrentDictionary<Guid, IncomingState> _states = new();
    private WebApplication? _app;

    public DirectTransferServer(
        SettingsStore settingsStore,
        AppSettings settings,
        HistoryStore history)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _history = history;
    }

    public event EventHandler<LocalTransferRecord>? OfferReceived;
    public event EventHandler<string>? ServerError;

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            return;
        }

        Port = NetworkUtilities.FindAvailablePort(
            LumenProtocol.DefaultDirectPort,
            LumenProtocol.DefaultDirectPort + 20);
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = "Lumen.Windows.DirectServer",
                EnvironmentName = "Production",
                ContentRootPath = _settingsStore.DataDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(Port);
                options.Limits.MaxRequestBodySize = LumenProtocol.MaxChunkSize + 1024 * 1024;
                options.AddServerHeader = false;
            });
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (DirectHttpException ex)
                {
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = ex.StatusCode;
                        await context.Response.WriteAsJsonAsync(
                            new ApiError(ex.Code, ex.Message),
                            cancellationToken: context.RequestAborted);
                    }
                }
                catch (Exception ex)
                {
                    ServerError?.Invoke(this, "局域网直连服务异常：" + ex.Message);
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsJsonAsync(
                            new ApiError("direct_server_error", "局域网直连服务处理失败。"),
                            cancellationToken: context.RequestAborted);
                    }
                }
            });

            app.MapGet(LumenRoutes.Ping, Ping);
            app.MapPost(LumenRoutes.Offers, (Delegate)CreateOfferAsync);
            app.MapGet($"{LumenRoutes.Offers}/{{transferId:guid}}", OfferStatus);
            app.MapPut(
                "/lumen/v1/transfers/{transferId:guid}/chunks/{index:int}",
                (Delegate)UploadChunkAsync);
            app.MapPost(
                "/lumen/v1/transfers/{transferId:guid}/complete",
                (Delegate)CompleteAsync);
            app.MapPost(
                "/lumen/v1/transfers/{transferId:guid}/cancel",
                (Delegate)CancelAsync);

            await app.StartAsync(ct);
            _app = app;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    public Task AcceptOfferAsync(Guid transferId, CancellationToken ct = default)
    {
        if (!_states.TryGetValue(transferId, out var state))
        {
            throw new InvalidOperationException("传输邀请不存在。");
        }

        if (state.Record.Status != TransferStatuses.Pending)
        {
            return Task.CompletedTask;
        }

        state.Record.Status = TransferStatuses.Accepted;
        state.Record.UpdatedAt = DateTimeOffset.UtcNow;
        state.Record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = "accepted",
            Message = "已接受局域网传输邀请。",
            Address = state.Record.RemoteAddress,
            IpFamily = state.Record.IpFamily
        });
        _history.SaveSoon();
        return Task.CompletedTask;
    }

    public Task DeclineOfferAsync(Guid transferId, CancellationToken ct = default)
    {
        if (!_states.TryGetValue(transferId, out var state))
        {
            throw new InvalidOperationException("传输邀请不存在。");
        }

        if (state.Record.Status != TransferStatuses.Pending)
        {
            return Task.CompletedTask;
        }

        state.Record.Status = TransferStatuses.Declined;
        state.Record.UpdatedAt = DateTimeOffset.UtcNow;
        state.Record.AddEvent(new TransferEventRecord
        {
            At = DateTimeOffset.UtcNow,
            Type = "declined",
            Message = "已拒绝局域网传输。"
        });
        _history.SaveSoon();
        return Task.CompletedTask;
    }

    private IResult Ping(HttpContext context)
    {
        var addresses = NetworkUtilities.GetLocalAddresses();
        return Results.Json(new DirectPingResponse(
            "lumen",
            LumenProtocol.Version,
            _settings.DeviceId,
            _settings.DeviceName,
            "windows",
            Environment.MachineName,
            Port,
            addresses.Select(x => x.Address).ToArray(),
            addresses.Select(x => x.Family).Distinct().ToArray()), LumenJson.Options);
    }

    private async Task<IResult> CreateOfferAsync(HttpContext context)
    {
        DirectOfferRequest? offer;
        try
        {
            offer = await JsonSerializer.DeserializeAsync<DirectOfferRequest>(
                context.Request.Body,
                LumenJson.Options,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            throw new DirectHttpException(400, "invalid_offer", "传输邀请 JSON 无效。");
        }

        if (offer is null ||
            offer.SenderDeviceId == Guid.Empty ||
            offer.SenderDeviceId == _settings.DeviceId ||
            offer.Kind is not (TransferKinds.File or TransferKinds.Image or TransferKinds.Text))
        {
            throw new DirectHttpException(400, "invalid_offer", "传输邀请参数无效。");
        }

        if (offer.Kind == TransferKinds.Text)
        {
            if (string.IsNullOrEmpty(offer.TextContent) ||
                Encoding.UTF8.GetByteCount(offer.TextContent) > 1024 * 1024)
            {
                throw new DirectHttpException(400, "invalid_text", "文字内容为空或超过 1 MiB。");
            }
        }
        else if (offer.Size < 0)
        {
            throw new DirectHttpException(400, "invalid_size", "文件大小无效。");
        }

        var transferId = Guid.NewGuid();
        var chunkSize = offer.Kind == TransferKinds.Text
            ? 0
            : Math.Clamp(offer.ChunkSize, 64 * 1024, LumenProtocol.MaxChunkSize);
        var totalChunks = offer.Kind == TransferKinds.Text || offer.Size == 0
            ? 0
            : (int)Math.Min(int.MaxValue, (offer.Size + chunkSize - 1) / chunkSize);
        var remoteIp = NetworkUtilities.NormalizeIp(context.Connection.RemoteIpAddress);
        var localIp = NetworkUtilities.NormalizeIp(context.Connection.LocalIpAddress);
        var now = DateTimeOffset.UtcNow;
        var record = new LocalTransferRecord
        {
            Id = transferId,
            Direction = "in",
            Route = TransferRoutes.Lan,
            Status = TransferStatuses.Pending,
            Kind = offer.Kind,
            Name = string.IsNullOrWhiteSpace(offer.Name) ? "Lumen 传输" : offer.Name,
            MimeType = string.IsNullOrWhiteSpace(offer.MimeType)
                ? "application/octet-stream"
                : offer.MimeType,
            Size = offer.Kind == TransferKinds.Text
                ? Encoding.UTF8.GetByteCount(offer.TextContent!)
                : offer.Size,
            Sha256 = offer.Sha256,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks,
            PeerName = string.IsNullOrWhiteSpace(offer.SenderName) ? "局域网设备" : offer.SenderName,
            PeerDeviceId = offer.SenderDeviceId.ToString(),
            PeerPlatform = offer.SenderPlatform,
            LocalAddress = localIp,
            RemoteAddress = remoteIp,
            IpFamily = NetworkUtilities.FamilyOf(remoteIp),
            TextContent = offer.Kind == TransferKinds.Text ? offer.TextContent : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        record.AddEvent(new TransferEventRecord
        {
            At = now,
            Type = "created",
            Message = "收到局域网直连传输邀请。",
            Address = remoteIp,
            IpFamily = NetworkUtilities.FamilyOf(remoteIp)
        });
        record.AddEvent(new TransferEventRecord
        {
            At = now,
            Type = "route.selected",
            Message = $"局域网直连 · {NetworkUtilities.FamilyOf(remoteIp)}，不经过服务器。",
            Address = remoteIp,
            IpFamily = NetworkUtilities.FamilyOf(remoteIp)
        });

        var state = new IncomingState(
            record,
            NewUploadToken(),
            Path.Combine(_settingsStore.DataDirectory, "incoming", transferId.ToString("N")));
        Directory.CreateDirectory(state.TempDirectory);
        _states[transferId] = state;
        _history.Add(record);
        UiThread.Post(() => OfferReceived?.Invoke(this, record));
        _ = ExpirePendingOfferAsync(transferId, state);

        return Results.Json(
            new DirectOfferResponse(transferId, TransferStatuses.Pending, state.UploadToken),
            LumenJson.Options);
    }

    private IResult OfferStatus(Guid transferId)
    {
        if (!_states.TryGetValue(transferId, out var state))
        {
            throw new DirectHttpException(404, "not_found", "传输邀请不存在。");
        }

        return Results.Json(new DirectOfferStatus(
            transferId,
            state.Record.Status,
            state.Record.Error,
            state.Record.UpdatedAt), LumenJson.Options);
    }

    private async Task<IResult> UploadChunkAsync(
        Guid transferId,
        int index,
        HttpContext context)
    {
        if (!_states.TryGetValue(transferId, out var state))
        {
            throw new DirectHttpException(404, "not_found", "传输不存在。");
        }

        RequireUploadToken(context, state, transferId);
        if (state.Record.Status is not (TransferStatuses.Accepted or TransferStatuses.Uploading))
        {
            throw new DirectHttpException(409, "invalid_state", "接收设备尚未接受传输。");
        }

        if (index < 0 || index >= state.Record.TotalChunks)
        {
            throw new DirectHttpException(400, "invalid_chunk", "分块序号超出范围。");
        }

        var expected = (int)Math.Min(
            state.Record.ChunkSize,
            state.Record.Size - (long)index * state.Record.ChunkSize);
        if (context.Request.ContentLength is { } length && length != expected)
        {
            throw new DirectHttpException(400, "chunk_size_mismatch", "分块长度与声明不一致。");
        }

        await state.UploadLock.WaitAsync(context.RequestAborted);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var path = Path.Combine(state.TempDirectory, $"{index:D8}.chunk");
            var temp = path + ".part";
            var expectedSha = context.Request.Headers["X-Lumen-Chunk-Sha256"].ToString();
            var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            var total = 0;
            try
            {
                await using (var target = new FileStream(
                    temp,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        total += read;
                        if (total > expected)
                        {
                            throw new DirectHttpException(413, "chunk_too_large", "分块内容超过声明大小。");
                        }

                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(
                            buffer.AsMemory(0, read),
                            context.RequestAborted);
                    }

                    await target.FlushAsync(context.RequestAborted);
                }

                if (total != expected)
                {
                    throw new DirectHttpException(
                        400,
                        "chunk_incomplete",
                        $"分块应为 {expected} 字节，实际收到 {total} 字节。");
                }

                var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(expectedSha) &&
                    !sha.Equals(expectedSha.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new DirectHttpException(400, "chunk_hash_mismatch", "分块哈希校验失败。");
                }

                File.Move(temp, path, overwrite: true);
                stopwatch.Stop();
                var speed = total / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                state.Uploaded[index] = total;
                state.Record.Status = TransferStatuses.Uploading;
                state.Record.CompletedChunks = state.Uploaded.Count;
                state.Record.TransferredBytes = state.Uploaded.Values.Sum(x => (long)x);
                state.Record.SpeedBps = state.Record.SpeedBps > 0
                    ? state.Record.SpeedBps * 0.65 + speed * 0.35
                    : speed;
                state.Record.UpdatedAt = DateTimeOffset.UtcNow;
                state.Record.AddEvent(new TransferEventRecord
                {
                    At = DateTimeOffset.UtcNow,
                    Type = "chunk.downloaded",
                    Message = $"已接收分块 {index + 1}/{state.Record.TotalChunks}。",
                    Bytes = total,
                    SpeedBps = speed,
                    Address = state.Record.RemoteAddress,
                    IpFamily = state.Record.IpFamily
                });
                _history.SaveSoon();
                return Results.Json(new ChunkUploadResultDto(
                    state.Record.CompletedChunks,
                    state.Record.TotalChunks,
                    state.Record.TransferredBytes,
                    speed), LumenJson.Options);
            }
            finally
            {
                hash.Dispose();
                if (File.Exists(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch
                    {
                    }
                }
            }
        }
        finally
        {
            state.UploadLock.Release();
        }
    }

    private async Task<IResult> CompleteAsync(Guid transferId, HttpContext context)
    {
        if (!_states.TryGetValue(transferId, out var state))
        {
            throw new DirectHttpException(404, "not_found", "传输不存在。");
        }

        RequireUploadToken(context, state, transferId);
        await state.UploadLock.WaitAsync(context.RequestAborted);
        try
        {
            if (state.Record.Status == TransferStatuses.Completed)
            {
                return Results.Ok(state.Record.Snapshot());
            }

            if (state.Record.Status is not (TransferStatuses.Accepted or TransferStatuses.Uploading))
            {
                throw new DirectHttpException(409, "invalid_state", "当前状态不能完成传输。");
            }

            if (state.Uploaded.Count != state.Record.TotalChunks ||
                state.Uploaded.Values.Sum(x => (long)x) != state.Record.Size)
            {
                throw new DirectHttpException(409, "transfer_incomplete", "分块尚未收齐。");
            }

            if (state.Record.Kind != TransferKinds.Text)
            {
                await MergeFileAsync(state, context.RequestAborted);
            }

            state.Record.Status = TransferStatuses.Completed;
            state.Record.CompletedAt = DateTimeOffset.UtcNow;
            state.Record.UpdatedAt = state.Record.CompletedAt.Value;
            state.Record.TransferredBytes = state.Record.Size;
            state.Record.SpeedBps = state.Record.SpeedBps;
            state.Record.AddEvent(new TransferEventRecord
            {
                At = state.Record.CompletedAt.Value,
                Type = "completed",
                Message = state.Record.Kind == TransferKinds.Text
                    ? "文字已接收。"
                    : "文件已合并并保存到接收目录。",
                Address = state.Record.RemoteAddress,
                IpFamily = state.Record.IpFamily
            });
            _history.SaveSoon();

            try
            {
                Directory.Delete(state.TempDirectory, true);
            }
            catch
            {
            }

            return Results.Ok(state.Record.Snapshot());
        }
        finally
        {
            state.UploadLock.Release();
        }
    }

    private async Task MergeFileAsync(IncomingState state, CancellationToken ct)
    {
        var finalPath = FileUtilities.ResolveIncomingPath(
            _settings.ReceiveDirectory,
            state.Record.Name);
        var temp = finalPath + ".lumenpart";
        try
        {
            await using (var output = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1024 * 1024];
                for (var index = 0; index < state.Record.TotalChunks; index++)
                {
                    var chunkPath = Path.Combine(state.TempDirectory, $"{index:D8}.chunk");
                    await using var source = new FileStream(
                        chunkPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        overall.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }

                await output.FlushAsync(ct);
                var sha = Convert.ToHexString(overall.GetHashAndReset()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(state.Record.Sha256) &&
                    !sha.Equals(state.Record.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DirectHttpException(
                        422,
                        "file_hash_mismatch",
                        "文件整体 SHA-256 校验失败，内容可能已损坏。");
                }

                state.Record.Sha256 = sha;
            }

            File.Move(temp, finalPath, overwrite: false);
            state.Record.LocalPath = finalPath;
            state.Record.AddEvent(new TransferEventRecord
            {
                At = DateTimeOffset.UtcNow,
                Type = "merged",
                Message = "分块已按顺序合并，SHA-256 校验通过。",
                Bytes = state.Record.Size,
                Address = state.Record.RemoteAddress,
                IpFamily = state.Record.IpFamily
            });
        }
        catch
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private Task<IResult> CancelAsync(Guid transferId, HttpContext context)
    {
        if (!_states.TryGetValue(transferId, out var state))
        {
            throw new DirectHttpException(404, "not_found", "传输不存在。");
        }

        RequireUploadToken(context, state, transferId);
        if (!TransferStatuses.IsTerminal(state.Record.Status))
        {
            state.Record.Status = TransferStatuses.Cancelled;
            state.Record.UpdatedAt = DateTimeOffset.UtcNow;
            state.Record.AddEvent(new TransferEventRecord
            {
                At = DateTimeOffset.UtcNow,
                Type = "cancelled",
                Message = "发送设备取消了局域网传输。"
            });
            _history.SaveSoon();
        }

        return Task.FromResult(Results.Ok(state.Record.Snapshot()));
    }

    private async Task ExpirePendingOfferAsync(Guid transferId, IncomingState state)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        if (state.Record.Status == TransferStatuses.Pending)
        {
            state.Record.Status = TransferStatuses.Declined;
            state.Record.UpdatedAt = DateTimeOffset.UtcNow;
            state.Record.AddEvent(new TransferEventRecord
            {
                At = DateTimeOffset.UtcNow,
                Type = "declined",
                Message = "传输邀请等待超时，已自动拒绝。"
            });
            _history.SaveSoon();
        }
    }

    private static void RequireUploadToken(
        HttpContext context,
        IncomingState state,
        Guid transferId)
    {
        var token = context.Request.Headers[LumenProtocol.UploadTokenHeader].ToString();
        if (string.IsNullOrEmpty(token) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token),
                Encoding.UTF8.GetBytes(state.UploadToken)))
        {
            throw new DirectHttpException(403, "upload_token_invalid", "上传凭证无效。");
        }
    }

    private static string NewUploadToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            await _app.DisposeAsync();
            _app = null;
        }
    }

    private sealed class IncomingState(
        LocalTransferRecord record,
        string uploadToken,
        string tempDirectory)
    {
        public LocalTransferRecord Record { get; } = record;
        public string UploadToken { get; } = uploadToken;
        public string TempDirectory { get; } = tempDirectory;
        public SemaphoreSlim UploadLock { get; } = new(1, 1);
        public Dictionary<int, int> Uploaded { get; } = new();
    }

    private sealed class DirectHttpException(int statusCode, string code, string message)
        : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Code { get; } = code;
    }
}
