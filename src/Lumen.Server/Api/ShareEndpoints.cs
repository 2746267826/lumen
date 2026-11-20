using System.Security.Claims;
using System.Text;
using Lumen.Server.Data;
using Lumen.Server.Domain;
using Lumen.Server.Infrastructure;
using Lumen.Server.Security;
using Lumen.Server.Services;
using Lumen.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Server.Api;

public static class ShareEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapPost("/shares/file", CreateFileAsync).RequireRateLimiting("share-create");
        api.MapPost("/shares/text", CreateTextAsync).RequireRateLimiting("share-create");
        api.MapGet("/shares", ListAsync);
        api.MapDelete("/shares/{shareId:guid}", RevokeAsync);

        app.MapGet("/api/public/shares/{token}", PublicMetadataAsync)
            .AllowAnonymous()
            .RequireRateLimiting("public-share");
        app.MapGet("/api/public/shares/{token}/download", PublicDownloadAsync)
            .AllowAnonymous()
            .RequireRateLimiting("public-share");
        app.MapGet("/api/public/shares/{token}/preview", PublicPreviewAsync)
            .AllowAnonymous()
            .RequireRateLimiting("public-share");
        app.MapGet("/s/{token}", SharePageAsync).AllowAnonymous();
        app.MapPost("/s/{token}", UnlockAsync).AllowAnonymous().RequireRateLimiting("share-unlock");
    }

    private static async Task<IResult> CreateFileAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        LumenDbContext db,
        StorageService storage,
        CryptoService crypto,
        IOptions<LumenOptions> options,
        string name,
        string? mime,
        int? expiresInHours,
        int? maxDownloads,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var password = context.Request.Headers[LumenProtocol.SharePasswordHeader].ToString();
        var hours = expiresInHours ?? 24;
        var downloadLimit = maxDownloads ?? 0;
        ApiSupport.ValidateShareOptions(hours, password, downloadLimit);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ApiErrors.BadRequest("name_required", "缺少分享名称。");
        }

        var length = context.Request.ContentLength;
        if (length is null or <= 0)
        {
            throw ApiErrors.BadRequest("empty_file", "分享文件不能为空。");
        }

        if (length > options.Value.MaxShareBytes)
        {
            throw ApiErrors.TooLarge("分享文件超过服务器允许的大小。");
        }

        var shareId = Guid.NewGuid();
        var path = storage.GetSharePath(shareId);
        var tempPath = path + ".part";
        var cleanName = ApiSupport.CleanFileName(name);
        var cleanMime = ApiSupport.SafeMime(mime);
        var token = crypto.NewToken(24);
        var buffer = new byte[128 * 1024];
        long written = 0;

        try
        {
            await using (var target = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > options.Value.MaxShareBytes)
                    {
                        throw ApiErrors.TooLarge("分享文件超过服务器允许的大小。");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await target.FlushAsync(ct);
            }

            if (written == 0)
            {
                throw ApiErrors.BadRequest("empty_file", "分享文件不能为空。");
            }

            if (length is { } declared && written != declared)
            {
                throw ApiErrors.BadRequest("upload_incomplete", "上传内容长度与声明不一致。");
            }

            File.Move(tempPath, path);
            var now = DateTimeOffset.UtcNow;
            var share = new Share
            {
                Id = shareId,
                UserId = userId,
                Token = token,
                Name = cleanName,
                Kind = cleanMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? TransferKinds.Image
                    : TransferKinds.File,
                MimeType = cleanMime,
                Size = written,
                StoragePath = path,
                PasswordHash = string.IsNullOrWhiteSpace(password) ? null : crypto.HashPassword(password),
                CreatedAt = now,
                ExpiresAt = now.AddHours(hours),
                MaxDownloads = downloadLimit
            };
            db.Shares.Add(share);
            await db.SaveChangesAsync(ct);
            return Results.Created(
                $"/api/v1/shares/{share.Id}",
                ApiSupport.ToShareDto(context, share, options.Value));
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            throw;
        }
    }

    private static async Task<IResult> CreateTextAsync(
        ShareTextRequest request,
        HttpContext context,
        ClaimsPrincipal principal,
        LumenDbContext db,
        CryptoService crypto,
        IOptions<LumenOptions> options,
        CancellationToken ct)
    {
        ApiSupport.ValidateShareOptions(
            request.ExpiresInHours,
            request.Password,
            request.MaxDownloads);
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw ApiErrors.BadRequest("empty_text", "文字内容不能为空。");
        }

        var bytes = Encoding.UTF8.GetByteCount(request.Text);
        if (bytes > 1024 * 1024)
        {
            throw ApiErrors.TooLarge("文字分享不能超过 1 MiB。");
        }

        var now = DateTimeOffset.UtcNow;
        var share = new Share
        {
            Id = Guid.NewGuid(),
            UserId = principal.GetUserId(),
            Token = crypto.NewToken(24),
            Name = ApiSupport.CleanFileName(request.Name),
            Kind = TransferKinds.Text,
            MimeType = "text/plain; charset=utf-8",
            Size = bytes,
            TextContent = request.Text,
            PasswordHash = string.IsNullOrWhiteSpace(request.Password)
                ? null
                : crypto.HashPassword(request.Password),
            CreatedAt = now,
            ExpiresAt = now.AddHours(request.ExpiresInHours),
            MaxDownloads = request.MaxDownloads
        };
        db.Shares.Add(share);
        await db.SaveChangesAsync(ct);
        return Results.Created(
            $"/api/v1/shares/{share.Id}",
            ApiSupport.ToShareDto(context, share, options.Value));
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        IOptions<LumenOptions> options,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var shares = await db.Shares
            .AsNoTracking()
            .Where(x => x.UserId == userId && !x.Revoked)
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        return Results.Ok(shares
            .Select(x => ApiSupport.ToShareDto(context, x, options.Value))
            .ToList());
    }

    private static async Task<IResult> RevokeAsync(
        Guid shareId,
        ClaimsPrincipal principal,
        LumenDbContext db,
        StorageService storage,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var share = await db.Shares.SingleOrDefaultAsync(
            x => x.Id == shareId && x.UserId == userId,
            ct) ?? throw ApiErrors.NotFound("分享不存在。");
        share.Revoked = true;
        await db.SaveChangesAsync(ct);
        storage.DeleteShare(share.Id, share.StoragePath);
        return Results.NoContent();
    }

    private static async Task<IResult> PublicMetadataAsync(
        string token,
        HttpContext context,
        LumenDbContext db,
        CryptoService crypto,
        CancellationToken ct)
    {
        var share = await GetActiveShareAsync(token, db, ct);
        if (ApiSupport.IsDownloadLimitReached(share))
        {
            throw new ApiException(410, "share_download_limit", "该分享已达到下载次数上限。");
        }

        var hasPassword = !string.IsNullOrWhiteSpace(share.PasswordHash);
        var authorized = ApiSupport.VerifySharePassword(context, share, crypto, allowCookie: true);
        if (!hasPassword)
        {
            authorized = true;
        }

        return Results.Ok(new PublicShareDto(
            share.Name,
            share.Kind,
            share.MimeType,
            share.Size,
            authorized && share.Kind == TransferKinds.Text ? share.TextContent : null,
            hasPassword && !authorized,
            share.ExpiresAt,
            share.DownloadCount,
            share.MaxDownloads));
    }

    private static async Task<IResult> PublicDownloadAsync(
        string token,
        HttpContext context,
        LumenDbContext db,
        CryptoService crypto,
        CancellationToken ct)
    {
        var share = await GetActiveShareAsync(token, db, ct);
        if (ApiSupport.IsDownloadLimitReached(share))
        {
            throw new ApiException(410, "share_download_limit", "该分享已达到下载次数上限。");
        }

        if (!ApiSupport.VerifySharePassword(context, share, crypto, allowCookie: true))
        {
            throw new ApiException(401, "share_password_required", "此分享需要密码。");
        }

        share.DownloadCount++;
        await db.SaveChangesAsync(ct);
        ApplyPublicHeaders(context);

        if (share.Kind == TransferKinds.Text)
        {
            var bytes = Encoding.UTF8.GetBytes(share.TextContent ?? "");
            return Results.File(bytes, "text/plain; charset=utf-8", $"{share.Name}.txt");
        }

        if (string.IsNullOrWhiteSpace(share.StoragePath) || !File.Exists(share.StoragePath))
        {
            throw ApiErrors.NotFound("分享文件已失效。");
        }

        var stream = new FileStream(
            share.StoragePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Results.File(stream, share.MimeType, share.Name);
    }

    private static async Task<IResult> PublicPreviewAsync(
        string token,
        HttpContext context,
        LumenDbContext db,
        CryptoService crypto,
        CancellationToken ct)
    {
        var share = await GetActiveShareAsync(token, db, ct);
        if (!ApiSupport.VerifySharePassword(context, share, crypto, allowCookie: true))
        {
            throw new ApiException(401, "share_password_required", "此分享需要密码。");
        }

        if (share.Kind != TransferKinds.Image ||
            share.MimeType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/avif" or "image/bmp") ||
            string.IsNullOrWhiteSpace(share.StoragePath) ||
            !File.Exists(share.StoragePath))
        {
            throw ApiErrors.NotFound("此分享没有可预览的图片。");
        }

        ApplyPublicHeaders(context);
        var stream = new FileStream(
            share.StoragePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Results.File(stream, share.MimeType, enableRangeProcessing: true);
    }

    private static async Task<IResult> SharePageAsync(
        string token,
        HttpContext context,
        LumenDbContext db,
        CryptoService crypto,
        CancellationToken ct)
    {
        var share = await db.Shares.AsNoTracking().SingleOrDefaultAsync(x => x.Token == token, ct);
        var error = context.Request.Query["error"].ToString();
        var result = SharePageRenderer.Render(share, context, crypto, error);
        if (result.NotFound)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        else if (result.Gone)
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
        }

        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data:; base-uri 'none'; form-action 'self'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Content(result.Html, "text/html; charset=utf-8");
    }

    private static async Task<IResult> UnlockAsync(
        string token,
        HttpContext context,
        LumenDbContext db,
        CryptoService crypto,
        CancellationToken ct)
    {
        var share = await db.Shares.AsNoTracking().SingleOrDefaultAsync(x => x.Token == token, ct);
        if (share is null)
        {
            return Results.NotFound();
        }

        if (ApiSupport.IsShareExpired(share) || ApiSupport.IsDownloadLimitReached(share))
        {
            return Results.Redirect($"/s/{token}");
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.Redirect($"/s/{token}?error=form");
        }

        var form = await context.Request.ReadFormAsync(ct);
        var password = form["password"].ToString();
        if (!crypto.VerifyPassword(password, share.PasswordHash))
        {
            await Task.Delay(350, ct);
            return Results.Redirect($"/s/{token}?error=password");
        }

        var options = new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = share.ExpiresAt,
            Path = $"/"
        };
        context.Response.Cookies.Append(
            ApiSupport.ShareCookieName(share.Token),
            crypto.CreateShareCookie(share.Token, share.PasswordHash),
            options);
        return Results.Redirect($"/s/{token}");
    }

    private static async Task<Share> GetActiveShareAsync(
        string token,
        LumenDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 80)
        {
            throw ApiErrors.NotFound("分享不存在。");
        }

        var share = await db.Shares
            .SingleOrDefaultAsync(x => x.Token == token, ct)
            ?? throw ApiErrors.NotFound("分享不存在。");
        if (share.Revoked)
        {
            throw ApiErrors.NotFound("分享不存在。");
        }

        if (share.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ApiException(410, "share_expired", "该分享已过期。");
        }

        return share;
    }

    private static void ApplyPublicHeaders(HttpContext context)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Cache-Control"] = "private, no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
