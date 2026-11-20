using System.Security.Claims;
using Lumen.Server.Data;
using Lumen.Server.Domain;
using Lumen.Server.Infrastructure;
using Lumen.Server.Security;
using Lumen.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Server.Api;

public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapPost("/auth/register", RegisterAsync).AllowAnonymous();
        api.MapPost("/auth/login", LoginAsync).AllowAnonymous();

        api.MapGet("/me", async (ClaimsPrincipal principal, LumenDbContext db, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct)
                ?? throw ApiErrors.Unauthorized();
            return Results.Ok(ToDto(user));
        }).RequireAuthorization();

        api.MapPost("/auth/logout", async (
            ClaimsPrincipal principal,
            LumenDbContext db,
            CancellationToken ct) =>
        {
            var raw = principal.FindFirstValue("session_id");
            if (Guid.TryParse(raw, out var sessionId))
            {
                var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);
                if (session is not null)
                {
                    db.Sessions.Remove(session);
                    await db.SaveChangesAsync(ct);
                }
            }

            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        LumenDbContext db,
        CryptoService crypto,
        IOptions<LumenOptions> options,
        CancellationToken ct)
    {
        if (!options.Value.AllowRegistration)
        {
            throw ApiErrors.Forbidden("服务器已关闭新账户注册。");
        }

        var username = NormalizeUsername(request.Username);
        ValidateCredentials(username, request.Password);
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? username
            : request.DisplayName.Trim();
        if (displayName.Length > 80)
        {
            throw ApiErrors.BadRequest("invalid_display_name", "显示名称不能超过 80 个字符。");
        }

        if (await db.Users.AnyAsync(x => x.Username == username, ct))
        {
            throw ApiErrors.Conflict("username_taken", "该用户名已被使用。");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = displayName,
            PasswordHash = crypto.HashPassword(request.Password),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);

        var auth = IssueSession(user, crypto, options.Value.SessionDays);
        db.Sessions.Add(auth.Session);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw ApiErrors.Conflict("username_taken", "该用户名已被使用。");
        }

        return Results.Created("/api/v1/me", new AuthResponse(auth.Token, auth.Session.ExpiresAt, ToDto(user)));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        LumenDbContext db,
        CryptoService crypto,
        IOptions<LumenOptions> options,
        CancellationToken ct)
    {
        var username = NormalizeUsername(request.Username);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == username, ct);
        if (user is null || !crypto.VerifyPassword(request.Password, user.PasswordHash))
        {
            throw ApiErrors.Unauthorized("用户名或密码错误。");
        }

        var auth = IssueSession(user, crypto, options.Value.SessionDays);
        db.Sessions.Add(auth.Session);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new AuthResponse(auth.Token, auth.Session.ExpiresAt, ToDto(user)));
    }

    private static (string Token, UserSession Session) IssueSession(User user, CryptoService crypto, int days)
    {
        var token = crypto.NewToken(32);
        var now = DateTimeOffset.UtcNow;
        return (token, new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = crypto.HashToken(token),
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Clamp(days, 1, 365)),
            LastUsedAt = now
        });
    }

    private static string NormalizeUsername(string username) =>
        (username ?? "").Trim().ToLowerInvariant();

    private static void ValidateCredentials(string username, string password)
    {
        if (username.Length is < 3 or > 32 ||
            username.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
        {
            throw ApiErrors.BadRequest(
                "invalid_username",
                "用户名需为 3 到 32 位字母、数字、点、下划线或连字符。");
        }

        if (password.Length is < 8 or > 128)
        {
            throw ApiErrors.BadRequest("invalid_password", "密码长度必须为 8 到 128 个字符。");
        }
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Username, user.DisplayName);
}
