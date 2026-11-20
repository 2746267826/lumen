using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Lumen.Server.Data;
using Lumen.Server.Domain;
using Lumen.Server.Infrastructure;
using Lumen.Server.Realtime;
using Lumen.Server.Security;
using Lumen.Server.Services;
using Lumen.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Server.Api;

public static class TransferEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapPost("/transfers", CreateAsync);
        api.MapGet("/transfers", ListAsync);
        api.MapGet("/transfers/{transferId:guid}", DetailsAsync);
        api.MapPost("/transfers/{transferId:guid}/accept", AcceptAsync);
        api.MapPost("/transfers/{transferId:guid}/decline", DeclineAsync);
        api.MapPut("/transfers/{transferId:guid}/chunks/{index:int}", UploadChunkAsync);
        api.MapGet("/transfers/{transferId:guid}/chunks/{index:int}", DownloadChunkAsync);
        api.MapGet("/transfers/{transferId:guid}/chunks", ListChunksAsync);
        api.MapPost("/transfers/{transferId:guid}/ready", ReadyAsync);
        api.MapPost("/transfers/{transferId:guid}/received", ReceivedAsync);
        api.MapPost("/transfers/{transferId:guid}/cancel", CancelAsync);
    }

    private static async Task<IResult> CreateAsync(
        TransferCreateRequest request,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        RealtimeHub hub,
        IOptions<LumenOptions> options,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var sender = await ApiSupport.RequireDeviceAsync(context, db, userId, ct);

        var kind = (request.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not (TransferKinds.File or TransferKinds.Image or TransferKinds.Text))
        {
            throw ApiErrors.BadRequest("invalid_kind", "传输类型必须是 file、image 或 text。");
        }

        var recipient = await db.Devices.SingleOrDefaultAsync(
            x => x.Id == request.RecipientDeviceId && x.UserId == userId,
            ct) ?? throw ApiErrors.NotFound("接收设备不存在或不属于当前账户。");

        if (!hub.IsOnline(recipient.Id))
        {
            throw ApiErrors.Conflict("recipient_offline", "接收设备当前未连接服务器，请等待其上线。");
        }

        var name = ApiSupport.CleanFileName(request.Name);
        var mime = ApiSupport.SafeMime(
            request.MimeType,
            kind == TransferKinds.Text ? "text/plain; charset=utf-8" : "application/octet-stream");
        var size = request.Size;
        var textContent = request.TextContent;
        var chunkSize = 0;
        var totalChunks = 0;
        string? sha256 = null;

        if (kind == TransferKinds.Text)
        {
            if (string.IsNullOrEmpty(textContent))
            {
                throw ApiErrors.BadRequest("empty_text", "文字内容不能为空。");
            }

            var textBytes = Encoding.UTF8.GetByteCount(textContent);
            if (textBytes > 1024 * 1024)
            {
                throw ApiErrors.TooLarge("文字消息不能超过 1 MiB。");
            }

            size = textBytes;
            mime = "text/plain; charset=utf-8";
        }
        else
        {
            if (size < 0 || size > options.Value.MaxTransferBytes)
            {
                throw ApiErrors.TooLarge("文件超过服务器允许的传输大小。");
            }

            chunkSize = request.ChunkSize <= 0 ? LumenProtocol.DefaultChunkSize : request.ChunkSize;
            if (chunkSize is < 64 * 1024 or > LumenProtocol.MaxChunkSize ||
                chunkSize > options.Value.ChunkMaxBytes)
            {
                throw ApiErrors.BadRequest("invalid_chunk_size", "分块大小必须在 64 KiB 到 8 MiB 之间。");
            }

            var count = size == 0 ? 0 : (size + chunkSize - 1) / chunkSize;
            if (count > int.MaxValue)
            {
                throw ApiErrors.TooLarge("文件分块数量超过限制。");
            }

            totalChunks = (int)count;
            sha256 = NormalizeSha256(request.Sha256);
            textContent = null;
        }

        var now = DateTimeOffset.UtcNow;
        var remoteIp = ApiSupport.GetRemoteIp(context);
        var transfer = new Transfer
        {
            Id = Guid.NewGuid(),
            SenderDeviceId = sender.Id,
            SenderDevice = sender,
            RecipientDeviceId = recipient.Id,
            RecipientDevice = recipient,
            Kind = kind,
            Name = name,
            MimeType = mime,
            Size = size,
            Sha256 = sha256,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks,
            Status = TransferStatuses.Pending,
            Route = TransferRoutes.Relay,
            SenderAddress = string.IsNullOrWhiteSpace(remoteIp) ? null : remoteIp,
            RecipientAddress = recipient.LastIp,
            TextContent = textContent,
            CreatedAt = now,
            UpdatedAt = now
        };
        transfer.Events.Add(new TransferEvent
        {
            Id = Guid.NewGuid(),
            Type = "created",
            Message = "传输邀请已发送，等待接收设备确认。",
            Address = remoteIp,
            IpFamily = StorageService.GetFamily(remoteIp),
            At = now
        });

        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(ct);

        var dto = DtoMapper.ToDto(transfer);
        await hub.SendToDeviceAsync(userId, recipient.Id, new
        {
            type = WireEvents.TransferCreated,
            transfer = dto,
            at = now
        }, ct);
        return Results.Created($"/api/v1/transfers/{transfer.Id}", dto);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        LumenDbContext db,
        string? status,
        int? limit,
        int? offset,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var skip = Math.Max(offset ?? 0, 0);

        var query = db.Transfers
            .AsNoTracking()
            .Include(x => x.SenderDevice)
            .Include(x => x.RecipientDevice)
            .Where(x => x.SenderDevice.UserId == userId || x.RecipientDevice.UserId == userId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
        var items = rows
            .Select(x => DtoMapper.ToDto(x) with { TextContent = null })
            .ToList();
        return Results.Ok(new TransferPageDto(items, total));
    }

    private static async Task<IResult> DetailsAsync(
        Guid transferId,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        CancellationToken ct)
    {
        var transfer = await ApiSupport.RequireTransferParticipantAsync(
            context,
            db,
            transferId,
            principal.GetUserId(),
            ct,
            includeEvents: true);
        var events = transfer.Events
            .OrderBy(x => x.At)
            .Select(DtoMapper.ToDto)
            .ToList();
        return Results.Ok(new TransferDetailsDto(DtoMapper.ToDto(transfer), events));
    }

    private static async Task<IResult> AcceptAsync(
        Guid transferId,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        RealtimeHub hub,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var device = await ApiSupport.RequireDeviceAsync(context, db, userId, ct);
        var transfer = await ApiSupport.RequireTransferParticipantAsync(
            context, db, transferId, userId, ct);
        if (transfer.RecipientDeviceId != device.Id)
        {
            throw ApiErrors.Forbidden("只有接收设备可以确认此传输。");
        }

        if (transfer.Status == TransferStatuses.Accepted)
        {
            return Results.Ok(DtoMapper.ToDto(transfer));
        }

        if (transfer.Status != TransferStatuses.Pending)
        {
            throw ApiErrors.Conflict("invalid_state", "当前传输状态不能接受。");
        }

        transfer.Status = TransferStatuses.Accepted;
        transfer.UpdatedAt = DateTimeOffset.UtcNow;
        var itemEvent = ApiSupport.MakeEvent("accepted", context, "接收设备已接受传输邀请。");
        itemEvent.TransferId = transfer.Id;
        db.TransferEvents.Add(itemEvent);
        await db.SaveChangesAsync(ct);

        var dto = DtoMapper.ToDto(transfer);
        await hub.BroadcastAsync(userId, null, new
        {
            type = WireEvents.TransferAccepted,
            transfer = dto,
            at = transfer.UpdatedAt
        }, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeclineAsync(
        Guid transferId,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        RealtimeHub hub,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var device = await ApiSupport.RequireDeviceAsync(context, db, userId, ct);
        var transfer = await ApiSupport.RequireTransferParticipantAsync(
            context, db, transferId, userId, ct);
        if (transfer.RecipientDeviceId != device.Id)
        {
            throw ApiErrors.Forbidden("只有接收设备可以拒绝此传输。");
        }

        if (transfer.Status != TransferStatuses.Pending)
        {
            throw ApiErrors.Conflict("invalid_state", "当前传输状态不能拒绝。");
        }

        transfer.Status = TransferStatuses.Declined;
        transfer.UpdatedAt = DateTimeOffset.UtcNow;
        var itemEvent = ApiSupport.MakeEvent("declined", context, "接收设备拒绝了传输。");
        itemEvent.TransferId = transfer.Id;
        db.TransferEvents.Add(itemEvent);
        await db.SaveChangesAsync(ct);

        await hub.BroadcastAsync(userId, null, new
        {
            type = WireEvents.TransferDeclined,
            transferId = transfer.Id,
            at = transfer.UpdatedAt
        }, ct);
        return Results.Ok(DtoMapper.ToDto(transfer));
    }

    private static async Task<IResult> UploadChunkAsync(
        Guid transferId,
        int index,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        StorageService storage,
        RealtimeHub hub,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var sender = await ApiSupport.RequireDeviceAsync(context, db, userId, ct);
        var transfer = await ApiSupport.RequireTransferParticipantAsync(
            context, db, transferId, userId, ct);
        if (transfer.SenderDeviceId != sender.Id)
        {
            throw ApiErrors.Forbidden("只有发送设备可以上传分块。");
        }

        if (transfer.Status is not (TransferStatuses.Accepted or TransferStatuses.Uploading))
        {
            throw ApiErrors.Conflict("invalid_state", "当前传输状态不能上传分块。");
        }

        if (index < 0 || index >= transfer.TotalChunks)
        {
            throw ApiErrors.BadRequest("invalid_chunk_index", "分块序号超出范围。");
        }

        var offset = (long)index * transfer.ChunkSize;
        var expectedSize = (int)Math.Min(transfer.ChunkSize, transfer.Size - offset);
        if (context.Request.ContentLength is { } contentLength && contentLength != expectedSize)
        {
            throw ApiErrors.BadRequest(
                "chunk_size_mismatch",
                $"分块 {index} 应为 {expectedSize} 字节，实际为 {contentLength} 字节。");
        }

        var stopwatch = Stopwatch.StartNew();
        var directory = storage.GetTransferDirectory(transfer.Id);
        Directory.CreateDirectory(directory);
        var finalPath = storage.GetChunkPath(transfer.Id, index);
        var tempPath = Path.Combine(directory, $".{index:D8}.{Guid.NewGuid():N}.part");
        var expectedSha = context.Request.Headers["X-Lumen-Chunk-Sha256"].ToString();
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var totalBytes = 0;

        try
        {
            await using (var target = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > expectedSize)
                    {
                        throw ApiErrors.TooLarge("分块内容超过声明的分块大小。");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await target.FlushAsync(ct);
            }

            if (totalBytes != expectedSize)
            {
                throw ApiErrors.BadRequest(
                    "chunk_incomplete",
                    $"分块 {index} 应为 {expectedSize} 字节，实际收到 {totalBytes} 字节。");
            }

            var chunkSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(expectedSha) &&
                !chunkSha.Equals(expectedSha.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw ApiErrors.BadRequest("chunk_hash_mismatch", "分块 SHA-256 校验失败。");
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(tempPath, finalPath);
            stopwatch.Stop();
            var speed = totalBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var chunk = await db.TransferChunks.FindAsync([transfer.Id, index], ct);
            if (chunk is null)
            {
                chunk = new TransferChunk
                {
                    TransferId = transfer.Id,
                    Index = index,
                    Size = totalBytes,
                    Sha256 = chunkSha,
                    UploadedAt = DateTimeOffset.UtcNow
                };
                db.TransferChunks.Add(chunk);
            }
            else
            {
                chunk.Size = totalBytes;
                chunk.Sha256 = chunkSha;
                chunk.UploadedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            var completedChunks = await db.TransferChunks.CountAsync(x => x.TransferId == transfer.Id, ct);
            var transferredBytes = await db.TransferChunks
                .Where(x => x.TransferId == transfer.Id)
                .SumAsync(x => (long)x.Size, ct);
            transfer.Status = TransferStatuses.Uploading;
            transfer.CompletedChunks = completedChunks;
            transfer.TransferredBytes = transferredBytes;
            transfer.AverageSpeedBps = transfer.AverageSpeedBps is { } previous
                ? previous * 0.65 + speed * 0.35
                : speed;
            transfer.UpdatedAt = DateTimeOffset.UtcNow;
            var itemEvent = ApiSupport.MakeEvent(
                "chunk.uploaded",
                context,
                $"服务端已接收分块 {index + 1}/{transfer.TotalChunks}。",
                totalBytes,
                speed);
            itemEvent.TransferId = transfer.Id;
            db.TransferEvents.Add(itemEvent);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            var dto = DtoMapper.ToDto(transfer);
            await hub.SendToDeviceAsync(userId, transfer.RecipientDeviceId, new
            {
                type = WireEvents.TransferProgress,
                transfer = dto,
                @event = DtoMapper.ToDto(itemEvent),
                at = transfer.UpdatedAt
            }, ct);

            return Results.Ok(new ChunkUploadResultDto(
                completedChunks,
                transfer.TotalChunks,
                transferredBytes,
                speed));
        }
        finally
        {
            hash.Dispose();
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task<IResult> DownloadChunkAsync(
        Guid transferId,
        int index,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        StorageService storage,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var transfer = await ApiSupport.RequireTransferParticipantAsync(
            context, db, transferId, userId, ct);
        if (index < 0 || index >= transfer.TotalChunks)
        {
            throw ApiErrors.BadRequest("invalid_chunk_index", "分块序号超出范围。");
        }

        if (transfer.Status is TransferStatuses.Pending or TransferStatuses.Declined)
        {
            throw ApiErrors.Conflict("invalid_state", "传输尚未被接受。");
        }

        var chunk = await db.TransferChunks
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TransferId == transfer.Id && x.Index == index, ct)
            ?? throw ApiErrors.NotFound("分块尚未上传。");
        var path = storage.GetChunkPath(transfer.Id, index);
        if (!File.Exists(path))
        {
            throw ApiErrors.NotFound("分块文件不存在。");
        }

        context.Response.Headers["X-Lumen-Chunk-Sha256"] = chunk.Sha256 ?? "";
        context.Response.Headers["X-Lumen-Chunk-Index"] = index.ToString();
        context.Response.Headers["X-Lumen-Chunk-Size"] = chunk.Size.ToString();
        return Results.File(
            path,
            "application/octet-stream",
            enableRangeProcessing: false);
    }

    private static async Task<IResult> ListChunksAsync(
        Guid transferId,
        ClaimsPrincipal principal,
        LumenDbContext db,
        CancellationToken ct)
    {
        var transfer = await db.Transfers.AsNoTracking()
            .Include(x => x.SenderDevice)
            .Include(x => x.RecipientDevice)
            .SingleOrDefaultAsync(x => x.Id == transferId, ct)
            ?? throw ApiErrors.NotFound("传输记录不存在。");
        var userId = principal.GetUserId();
        if (transfer.SenderDevice.UserId != userId && transfer.RecipientDevice.UserId != userId)
        {
            throw ApiErrors.Forbidden();
        }

        var chunks = await db.TransferChunks
            .AsNoTracking()
            .Where(x => x.TransferId == transferId)
            .OrderBy(x => x.Index)
            .Select(x => new ChunkDownloadInfoDto(x.Index, x.Size, x.Sha256, x.UploadedAt))
            .ToListAsync(ct);
        return Results.Ok(new TransferChunksDto(
            transfer.Id,
            transfer.ChunkSize,
            transfer.TotalChunks,
            chunks));
    }

    private static async Task<IResult> ReadyAsync(
        Guid transferId,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        RealtimeHub hub,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var sender = await ApiSupport.RequireDeviceAsync(context, db, userId, ct);
        var transfer = await ApiSupport.RequireTransferParticipantAsync(
            context, db, transferId, userId, ct);
        if (transfer.SenderDeviceId != sender.Id)
        {
            throw ApiErrors.Forbidden("只有发送设备可以完成上传。");
        }

        if (transfer.Status == TransferStatuses.Ready)
        {
            return Results.Ok(DtoMapper.ToDto(transfer));
        }

        if (transfer.Status is not (TransferStatuses.Accepted or TransferStatuses.Uploading))
        {
            throw ApiErrors.Conflict("invalid_state", "当前传输状态不能标记为就绪。");
        }

        var completedChunks = transfer.Kind == TransferKinds.Text
            ? 0
            : await db.TransferChunks.CountAsync(x => x.TransferId == transfer.Id, ct);
        var uploadedBytes = transfer.Kind == TransferKinds.Text
            ? transfer.Size
            : await db.TransferChunks
                .Where(x => x.TransferId == transfer.Id)
                .SumAsync(x => (long)x.Size, ct);
        if (completedChunks != transfer.TotalChunks || uploadedBytes != transfer.Size)
        {
            throw ApiErrors.Conflict(
                "transfer_incomplete",
                $"上传尚未完成：{completedChunks}/{transfer.TotalChunks} 个分块。");
        }

        transfer.Status = TransferStatuses.Ready;
        transfer.CompletedChunks = completedChunks;
        transfer.TransferredBytes = uploadedBytes;
        transfer.UpdatedAt = DateTimeOffset.UtcNow;
        var itemEvent = ApiSupport.MakeEvent(
            "ready",
            context,
            "服务端已收齐全部内容，接收设备可以下载。");
        itemEvent.TransferId = transfer.Id;
        db.TransferEvents.Add(itemEvent);
        await db.SaveChangesAsync(ct);

        var dto = DtoMapper.ToDto(transfer);
        await hub.SendToDeviceAsync(userId, transfer.RecipientDeviceId, new
        {
            type = WireEvents.TransferReady,
            transfer = dto,
            at = transfer.UpdatedAt
        }, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> ReceivedAsync(
        Guid transferId,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        RealtimeHub hub,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var recipient = await ApiSupport.RequireDeviceAsync(context, db, userId, ct);
        var transfer = await ApiSupport.RequireTransferParticipantAsync(
            context, db, transferId, userId, ct);
        if (transfer.RecipientDeviceId != recipient.Id)
        {
            throw ApiErrors.Forbidden("只有接收设备可以确认接收完成。");
        }

        if (transfer.Status == TransferStatuses.Completed)
        {
            return Results.Ok(DtoMapper.ToDto(transfer));
        }

        if (transfer.Status != TransferStatuses.Ready)
        {
            throw ApiErrors.Conflict("invalid_state", "内容尚未就绪，不能确认接收完成。");
        }

        transfer.Status = TransferStatuses.Completed;
        transfer.CompletedAt = DateTimeOffset.UtcNow;
        transfer.UpdatedAt = transfer.CompletedAt.Value;
        transfer.TransferredBytes = transfer.Size;
        var itemEvent = ApiSupport.MakeEvent(
            "completed",
            context,
            "接收设备已完整接收全部内容。");
        itemEvent.TransferId = transfer.Id;
        db.TransferEvents.Add(itemEvent);
        await db.SaveChangesAsync(ct);

        var dto = DtoMapper.ToDto(transfer);
        await hub.BroadcastAsync(userId, null, new
        {
            type = WireEvents.TransferCompleted,
            transfer = dto,
            at = transfer.UpdatedAt
        }, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> CancelAsync(
        Guid transferId,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        RealtimeHub hub,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        await ApiSupport.RequireDeviceAsync(context, db, userId, ct);
        var transfer = await ApiSupport.RequireTransferParticipantAsync(
            context, db, transferId, userId, ct);
        if (transfer.Status is not (
            TransferStatuses.Pending or
            TransferStatuses.Accepted or
            TransferStatuses.Uploading or
            TransferStatuses.Ready))
        {
            throw ApiErrors.Conflict("invalid_state", "当前传输状态不能取消。");
        }

        transfer.Status = TransferStatuses.Cancelled;
        transfer.UpdatedAt = DateTimeOffset.UtcNow;
        var itemEvent = ApiSupport.MakeEvent(
            "cancelled",
            context,
            "传输已取消。");
        itemEvent.TransferId = transfer.Id;
        db.TransferEvents.Add(itemEvent);
        await db.SaveChangesAsync(ct);

        await hub.BroadcastAsync(userId, null, new
        {
            type = WireEvents.TransferCancelled,
            transferId = transfer.Id,
            transfer = DtoMapper.ToDto(transfer),
            at = transfer.UpdatedAt
        }, ct);
        return Results.Ok(DtoMapper.ToDto(transfer));
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !char.IsAsciiHexDigit(c)))
        {
            throw ApiErrors.BadRequest("invalid_sha256", "SHA-256 格式无效。");
        }

        return normalized;
    }
}
