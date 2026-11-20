using System.Security.Claims;
using System.Text.Encodings.Web;
using Lumen.Server.Data;
using Lumen.Server.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Server.Security;

public sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LumenDbContext db,
    CryptoService crypto)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LumenToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = crypto.HashToken(token);
        var now = DateTimeOffset.UtcNow;
        var session = await db.Sessions
            .AsNoTracking()
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == hash, Context.RequestAborted);

        if (session is null || session.ExpiresAt <= now)
        {
            return AuthenticateResult.Fail("Invalid or expired token.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new Claim(ClaimTypes.Name, session.User.DisplayName),
            new Claim("session_id", session.Id.ToString())
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private string? ReadToken()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        if (Request.Path.StartsWithSegments("/api/v1/ws"))
        {
            return Request.Query["access_token"].ToString();
        }

        return null;
    }
}

public static class ClaimsExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id)
            ? id
            : throw ApiErrors.Unauthorized();
    }
}
