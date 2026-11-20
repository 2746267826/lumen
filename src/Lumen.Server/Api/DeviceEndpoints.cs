using System.Security.Claims;
using Lumen.Server.Data;
using Lumen.Server.Domain;
using Lumen.Server.Infrastructure;
using Lumen.Server.Realtime;
using Lumen.Server.Security;
using Lumen.Server.Services;
using Lumen.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Server.Api;

public static class DeviceEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapPost("/devices", RegisterAsync);
        api.MapGet("/devices", ListAsync);
        api.MapDelete("/devices/{deviceId:guid}", DeleteAsync);

        app.Map("/api/v1/ws", WebSocketAsync).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(
        DeviceRegisterRequest request,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        RealtimeHub hub,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
        {
            throw ApiErrors.BadRequest("invalid_device_name", "设备名称需为 1 到 120 个字符。");
        }

        var platform = (request.Platform ?? "").Trim().ToLowerInvariant();
        if (platform is not ("windows" or "android" or "linux" or "macos" or "ios"))
        {
            throw ApiErrors.BadRequest("invalid_platform", "不支持的设备平台。");
        }

        var now = DateTimeOffset.UtcNow;
        var remoteIp = ApiSupport.GetRemoteIp(context);
        Device? device = null;
        if (request.DeviceId is { } requestedId && requestedId != Guid.Empty)
        {
            device = await db.Devices.SingleOrDefaultAsync(x => x.Id == requestedId, ct);
            if (device is not null && device.UserId != userId)
            {
                throw ApiErrors.Conflict("device_already_bound", "该设备标识已绑定到其他账户。");
            }
        }

        if (device is null)
        {
            device = new Device
            {
                Id = request.DeviceId is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now
            };
            db.Devices.Add(device);
        }

        device.Name = request.Name.Trim();
        device.Platform = platform;
        device.Model = Truncate(request.Model, 120);
        device.AppVersion = Truncate(request.AppVersion, 40);
        device.LastSeenAt = now;
        device.LastIp = string.IsNullOrWhiteSpace(remoteIp) ? device.LastIp : remoteIp;
        device.LastIpFamily = StorageService.GetFamily(remoteIp);
        await db.SaveChangesAsync(ct);

        var dto = DtoMapper.ToDto(device, hub.IsOnline(device.Id));
        return Results.Ok(new DeviceRegistrationResponse(dto));
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        LumenDbContext db,
        RealtimeHub hub,
        CancellationToken ct)
    {
        var devices = await ApiSupport.GetDeviceDtosAsync(db, hub, principal.GetUserId(), ct);
        return Results.Ok(devices);
    }

    private static async Task<IResult> DeleteAsync(
        Guid deviceId,
        ClaimsPrincipal principal,
        HttpContext context,
        LumenDbContext db,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var currentDeviceId = ApiSupport.GetDeviceId(context);
        if (deviceId == currentDeviceId)
        {
            throw ApiErrors.BadRequest("cannot_delete_current_device", "不能删除当前正在使用的设备。");
        }

        var device = await db.Devices.SingleOrDefaultAsync(
            x => x.Id == deviceId && x.UserId == userId,
            ct) ?? throw ApiErrors.NotFound("设备不存在。");

        var hasTransfers = await db.Transfers.AnyAsync(
            x => x.SenderDeviceId == deviceId || x.RecipientDeviceId == deviceId,
            ct);
        if (hasTransfers)
        {
            throw ApiErrors.Conflict(
                "device_has_history",
                "设备已有传输记录，暂不能删除。可以改名后继续使用。");
        }

        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task WebSocketAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        LumenDbContext db,
        RealtimeHub hub,
        CancellationToken ct)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new ApiError("websocket_required", "此端点仅接受 WebSocket 连接。"),
                cancellationToken: ct);
            return;
        }

        var userId = principal.GetUserId();
        if (!Guid.TryParse(context.Request.Query["deviceId"], out var deviceId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new ApiError("device_required", "缺少有效的 deviceId。"),
                cancellationToken: ct);
            return;
        }

        var device = await db.Devices.SingleOrDefaultAsync(
            x => x.Id == deviceId && x.UserId == userId,
            ct);
        if (device is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new ApiError("device_not_bound", "该设备未绑定到当前账户。"),
                cancellationToken: ct);
            return;
        }

        var remoteIp = ApiSupport.GetRemoteIp(context);
        device.LastSeenAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            device.LastIp = remoteIp;
            device.LastIpFamily = StorageService.GetFamily(remoteIp);
        }

        await db.SaveChangesAsync(ct);
        var devices = (await ApiSupport.GetDeviceDtosAsync(db, hub, userId, ct))
            .Select(x => x.Id == device.Id ? x with { Online = true } : x)
            .ToList();
        var deviceDto = DtoMapper.ToDto(device, true);
        await hub.RunAsync(context, userId, deviceDto, devices);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null :
        value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
