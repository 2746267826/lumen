using System.Security.Claims;
using Lumen.Server.Data;
using Lumen.Server.Domain;
using Lumen.Server.Infrastructure;
using Lumen.Server.Realtime;
using Lumen.Server.Services;
using Lumen.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Server.Api;

public static class ApiSupport
{
    public static Guid GetDeviceId(HttpContext context)
    {
        var raw = context.Request.Headers[LumenProtocol.DeviceHeader].ToString();
        return Guid.TryParse(raw, out var id)
            ? id
            : throw ApiErrors.BadRequest("device_header_required", "请求缺少有效的设备标识。");
    }

    public static async Task<Device> RequireDeviceAsync(
        HttpContext context,
        LumenDbContext db,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var deviceId = GetDeviceId(context);
        var device = await db.Devices.SingleOrDefaultAsync(
            x => x.Id == deviceId && x.UserId == userId,
            cancellationToken);
        return device ?? throw ApiErrors.Forbidden("该设备未绑定到当前账户。");
    }

    public static async Task<Transfer> RequireTransferParticipantAsync(
        HttpContext context,
        LumenDbContext db,
        Guid transferId,
        Guid userId,
        CancellationToken cancellationToken,
        bool includeEvents = false)
    {
        IQueryable<Transfer> query = db.Transfers
            .Include(x => x.SenderDevice)
            .Include(x => x.RecipientDevice);
        if (includeEvents)
        {
            query = query.Include(x => x.Events);
        }

        var transfer = await query.SingleOrDefaultAsync(x => x.Id == transferId, cancellationToken)
            ?? throw ApiErrors.NotFound("传输记录不存在。");

        if (transfer.SenderDevice.UserId != userId && transfer.RecipientDevice.UserId != userId)
        {
            throw ApiErrors.Forbidden("无权访问该传输。");
        }

        return transfer;
    }

    public static string GetRemoteIp(HttpContext context) =>
        StorageService.NormalizeRemoteIp(context.Connection.RemoteIpAddress);

    public static string CleanFileName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "Lumen 传输" : name.Trim();
        value = new string(value
            .Where(c => !char.IsControl(c) && c != '/' && c != '\\')
            .Take(240)
            .ToArray());
        return string.IsNullOrWhiteSpace(value) ? "Lumen 传输" : value;
    }

    public static string SafeMime(string? mime, string fallback = "application/octet-stream")
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return fallback;
        }

        var trimmed = mime.Trim();
        return trimmed.Length <= 180 && trimmed.Contains('/')
            ? trimmed
            : fallback;
    }

    public static TransferEvent MakeEvent(
        string type,
        HttpContext context,
        string? message = null,
        long? bytes = null,
        double? speedBps = null,
        string? ip = null)
    {
        var address = ip ?? GetRemoteIp(context);
        return new TransferEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            Message = message,
            Address = string.IsNullOrWhiteSpace(address) ? null : address,
            IpFamily = StorageService.GetFamily(address),
            Bytes = bytes,
            SpeedBps = speedBps,
            At = DateTimeOffset.UtcNow
        };
    }

    public static async Task<List<DeviceDto>> GetDeviceDtosAsync(
        LumenDbContext db,
        RealtimeHub hub,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var devices = await db.Devices
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.LastSeenAt)
            .ToListAsync(cancellationToken);
        return devices.Select(x => DtoMapper.ToDto(x, hub.IsOnline(x.Id))).ToList();
    }

    public static string BuildPublicShareUrl(HttpContext context, string token, LumenOptions options)
    {
        var baseUrl = string.IsNullOrWhiteSpace(options.PublicUrl)
            ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"
            : options.PublicUrl.TrimEnd('/');
        return $"{baseUrl}/s/{token}";
    }

    public static ShareDto ToShareDto(HttpContext context, Share share, LumenOptions options) => new(
        share.Id,
        BuildPublicShareUrl(context, share.Token, options),
        share.Name,
        share.Kind,
        share.MimeType,
        share.Size,
        !string.IsNullOrWhiteSpace(share.PasswordHash),
        share.ExpiresAt,
        share.MaxDownloads,
        share.DownloadCount,
        share.CreatedAt);

    public static string? ReadSharePassword(HttpContext context)
    {
        var header = context.Request.Headers[LumenProtocol.SharePasswordHeader].ToString();
        if (!string.IsNullOrEmpty(header))
        {
            return header;
        }

        return context.Request.Query["password"].ToString();
    }

    public static bool IsShareExpired(Share share) =>
        share.Revoked || share.ExpiresAt <= DateTimeOffset.UtcNow;

    public static bool IsDownloadLimitReached(Share share) =>
        share.MaxDownloads > 0 && share.DownloadCount >= share.MaxDownloads;

    public static bool VerifySharePassword(
        HttpContext context,
        Share share,
        CryptoService crypto,
        bool allowCookie)
    {
        if (string.IsNullOrWhiteSpace(share.PasswordHash))
        {
            return true;
        }

        var password = ReadSharePassword(context);
        if (!string.IsNullOrEmpty(password) && crypto.VerifyPassword(password, share.PasswordHash))
        {
            return true;
        }

        if (!allowCookie)
        {
            return false;
        }

        var cookieName = ShareCookieName(share.Token);
        return context.Request.Cookies.TryGetValue(cookieName, out var cookie) &&
               crypto.VerifyShareCookie(share.Token, share.PasswordHash, cookie);
    }

    public static string ShareCookieName(string token) =>
        $"lumen_share_{token[..Math.Min(12, token.Length)]}";

    public static void ValidateExpiryHours(int hours)
    {
        if (hours is < 1 or > 8760)
        {
            throw ApiErrors.BadRequest("invalid_expiry", "有效期必须在 1 到 8760 小时之间。");
        }
    }

    public static void ValidateShareOptions(int expiresInHours, string? password, int maxDownloads)
    {
        ValidateExpiryHours(expiresInHours);
        if (password is not null && password.Length is > 0 and (< 4 or > 64))
        {
            throw ApiErrors.BadRequest("invalid_password", "分享密码长度必须为 4 到 64 个字符。");
        }

        if (maxDownloads is < 0 or > 1_000_000)
        {
            throw ApiErrors.BadRequest("invalid_download_limit", "下载次数限制无效。");
        }
    }
}
