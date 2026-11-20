using System.Text.Json;
using System.Threading.RateLimiting;
using Lumen.Server.Api;
using Lumen.Server.Data;
using Lumen.Server.Infrastructure;
using Lumen.Server.Realtime;
using Lumen.Server.Security;
using Lumen.Server.Services;
using Lumen.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<LumenOptions>(builder.Configuration.GetSection("Lumen"));
var bootOptions = builder.Configuration.GetSection("Lumen").Get<LumenOptions>() ?? new LumenOptions();
bootOptions.DataDir = Path.GetFullPath(Path.IsPathRooted(bootOptions.DataDir)
    ? bootOptions.DataDir
    : Path.Combine(builder.Environment.ContentRootPath, bootOptions.DataDir));
Directory.CreateDirectory(bootOptions.DataDir);

var databasePath = Path.Combine(bootOptions.DataDir, "lumen.db");
builder.Services.AddDbContext<LumenDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath};Cache=Shared"));

builder.Services.AddSingleton(bootOptions);
builder.Services.AddSingleton<CryptoService>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<RealtimeHub>();
builder.Services.AddHostedService<CleanupService>();

builder.Services
    .AddAuthentication(TokenAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("share-create", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddPolicy("public-share", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 180,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("share-unlock", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{context.Connection.RemoteIpAddress}|{context.Request.RouteValues["token"]}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0
            }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ApiError("rate_limited", "请求过于频繁，请稍后再试。"),
            cancellationToken: ct);
    };
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null;
    options.AddServerHeader = false;
});

var app = builder.Build();
app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ApiException ex)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(
                new ApiError(ex.Code, ex.Message),
                cancellationToken: context.RequestAborted);
        }
    }
    catch (BadHttpRequestException ex)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(
                new ApiError("bad_request", ex.Message),
                cancellationToken: context.RequestAborted);
        }
    }
    catch (JsonException)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new ApiError("invalid_json", "请求 JSON 格式无效。"),
                cancellationToken: context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled request failure on {Method} {Path}", context.Request.Method, context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new ApiError("internal_error", "服务器处理请求时发生错误。"),
                cancellationToken: CancellationToken.None);
        }
    }
});

app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    if (response.HasStarted || response.ContentLength is > 0)
    {
        return;
    }

    var code = response.StatusCode switch
    {
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        _ => "http_error"
    };
    var message = response.StatusCode switch
    {
        401 => "请先登录。",
        403 => "没有权限执行此操作。",
        404 => "请求的资源不存在。",
        _ => $"HTTP {response.StatusCode}"
    };
    await response.WriteAsJsonAsync(new ApiError(code, message));
});

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new
{
    name = "Lumen Relay",
    version = "1.0.0",
    protocol = LumenProtocol.Version,
    status = "ok"
})).AllowAnonymous();

app.MapGet("/api/v1/health", () => Results.Ok(
    new HealthResponse("ok", "1.0.0", DateTimeOffset.UtcNow))).AllowAnonymous();

AuthEndpoints.Map(app);
DeviceEndpoints.Map(app);
TransferEndpoints.Map(app);
ShareEndpoints.Map(app);

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
}

app.Logger.LogInformation("Lumen relay listening. Data directory: {DataDir}", bootOptions.DataDir);
await app.RunAsync();

public partial class Program;
