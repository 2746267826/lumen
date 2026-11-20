using Lumen.Server.Domain;
using Lumen.Shared;

namespace Lumen.Server.Services;

public static class DtoMapper
{
    public static DeviceDto ToDto(Device device, bool online) => new(
        device.Id,
        device.UserId,
        device.Name,
        device.Platform,
        device.Model,
        online,
        device.LastSeenAt,
        device.LastIp,
        device.LastIpFamily,
        device.AppVersion);

    public static TransferDto ToDto(Transfer transfer) => new(
        transfer.Id,
        transfer.SenderDeviceId,
        transfer.RecipientDeviceId,
        transfer.SenderDevice?.Name ?? "发送设备",
        transfer.RecipientDevice?.Name ?? "接收设备",
        transfer.Kind,
        transfer.Name,
        transfer.MimeType,
        transfer.Size,
        transfer.Sha256,
        transfer.ChunkSize,
        transfer.TotalChunks,
        transfer.CompletedChunks,
        transfer.TransferredBytes,
        transfer.Status,
        transfer.Route,
        transfer.SenderAddress,
        transfer.RecipientAddress,
        transfer.TextContent,
        transfer.Error,
        transfer.CreatedAt,
        transfer.UpdatedAt,
        transfer.CompletedAt,
        transfer.AverageSpeedBps);

    public static TransferEventDto ToDto(TransferEvent item) => new(
        item.Id,
        item.Type,
        item.Message,
        item.Address,
        item.IpFamily ?? StorageService.GetFamily(item.Address),
        item.Bytes,
        item.SpeedBps,
        item.At);
}
