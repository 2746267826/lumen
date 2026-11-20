using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Shared;

public static class LumenProtocol
{
    public const int Version = 1;
    public const string ServiceName = "_lumen._tcp.local";
    public const string DiscoveryMagic = "LUMEN1";
    public const string MulticastGroupV4 = "239.255.77.88";
    public const string MulticastGroupV6 = "ff12::4c75:6d65";
    public const int DiscoveryPort = 47654;
    public const int DefaultDirectPort = 47655;
    public const int DefaultChunkSize = 1024 * 1024;
    public const int MaxChunkSize = 8 * 1024 * 1024;
    public const string DeviceHeader = "X-Lumen-Device";
    public const string UploadTokenHeader = "X-Lumen-Upload-Token";
    public const string SharePasswordHeader = "X-Share-Password";
    public const string UserAgent = "Lumen/1.0";
}

public static class LumenRoutes
{
    public const string Ping = "/lumen/v1/ping";
    public const string Offers = "/lumen/v1/offers";
    public const string Chunks = "/lumen/v1/transfers";
}

public static class TransferKinds
{
    public const string File = "file";
    public const string Image = "image";
    public const string Text = "text";
}

public static class TransferStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Uploading = "uploading";
    public const string Ready = "ready";
    public const string Completed = "completed";
    public const string Declined = "declined";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) =>
        status is Completed or Declined or Cancelled or Failed;
}

public static class TransferRoutes
{
    public const string Lan = "lan";
    public const string Relay = "relay";
}

public static class LumenJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions PrettyOptions = new(Options)
    {
        WriteIndented = true
    };
}

public sealed record RegisterRequest(string Username, string Password, string DisplayName);

public sealed record LoginRequest(string Username, string Password);

public sealed record UserDto(Guid Id, string Username, string DisplayName);

public sealed record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);

public sealed record DeviceRegisterRequest(
    Guid? DeviceId,
    string Name,
    string Platform,
    string? Model,
    string? AppVersion);

public sealed record DeviceDto(
    Guid Id,
    Guid UserId,
    string Name,
    string Platform,
    string? Model,
    bool Online,
    DateTimeOffset LastSeenAt,
    string? LastIp,
    string? LastIpFamily,
    string? AppVersion);

public sealed record DeviceRegistrationResponse(DeviceDto Device);

public sealed record TransferCreateRequest(
    Guid RecipientDeviceId,
    string Kind,
    string Name,
    string MimeType,
    long Size,
    string? Sha256,
    int ChunkSize,
    string? TextContent);

public sealed record TransferDto(
    Guid Id,
    Guid SenderDeviceId,
    Guid RecipientDeviceId,
    string SenderDeviceName,
    string RecipientDeviceName,
    string Kind,
    string Name,
    string MimeType,
    long Size,
    string? Sha256,
    int ChunkSize,
    int TotalChunks,
    int CompletedChunks,
    long TransferredBytes,
    string Status,
    string Route,
    string? SenderAddress,
    string? RecipientAddress,
    string? TextContent,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    double? AverageSpeedBps);

public sealed record TransferEventDto(
    Guid Id,
    string Type,
    string? Message,
    string? Address,
    string? IpFamily,
    long? Bytes,
    double? SpeedBps,
    DateTimeOffset At);

public sealed record TransferDetailsDto(TransferDto Transfer, IReadOnlyList<TransferEventDto> Events);

public sealed record TransferPageDto(IReadOnlyList<TransferDto> Items, int Total);

public sealed record ChunkUploadResultDto(
    int CompletedChunks,
    int TotalChunks,
    long TransferredBytes,
    double? SpeedBps);

public sealed record ChunkDownloadInfoDto(
    int Index,
    int Size,
    string? Sha256,
    DateTimeOffset UploadedAt);

public sealed record TransferChunksDto(
    Guid TransferId,
    int ChunkSize,
    int TotalChunks,
    IReadOnlyList<ChunkDownloadInfoDto> Chunks);

public sealed record ShareTextRequest(
    string Name,
    string Text,
    int ExpiresInHours,
    string? Password,
    int MaxDownloads);

public sealed record ShareDto(
    Guid Id,
    string Url,
    string Name,
    string Kind,
    string MimeType,
    long Size,
    bool PasswordProtected,
    DateTimeOffset ExpiresAt,
    int MaxDownloads,
    int DownloadCount,
    DateTimeOffset CreatedAt);

public sealed record PublicShareDto(
    string Name,
    string Kind,
    string MimeType,
    long Size,
    string? Text,
    bool PasswordRequired,
    DateTimeOffset ExpiresAt,
    int DownloadCount,
    int MaxDownloads);

public sealed record DirectPingResponse(
    string Service,
    int Version,
    Guid DeviceId,
    string Name,
    string Platform,
    string? Model,
    int Port,
    string[] Addresses,
    string[] Families);

public sealed record DiscoveryPacket(
    string Magic,
    int Version,
    Guid DeviceId,
    string Name,
    string Platform,
    string? Model,
    int Port,
    string[] Addresses,
    string[] Families,
    DateTimeOffset SeenAt,
    string? RelayUrl,
    Guid? UserId);

public sealed record DirectOfferRequest(
    Guid SenderDeviceId,
    string SenderName,
    string SenderPlatform,
    string Kind,
    string Name,
    string MimeType,
    long Size,
    string? Sha256,
    int ChunkSize,
    string? TextContent,
    DateTimeOffset CreatedAt);

public sealed record DirectOfferResponse(
    Guid TransferId,
    string Status,
    string UploadToken);

public sealed record DirectOfferStatus(
    Guid TransferId,
    string Status,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed record ApiError(string Code, string Message);

public sealed record HealthResponse(string Status, string Version, DateTimeOffset Time);

public static class WireEvents
{
    public const string Hello = "hello";
    public const string PresenceSnapshot = "presence.snapshot";
    public const string DeviceOnline = "device.online";
    public const string DeviceOffline = "device.offline";
    public const string TransferCreated = "transfer.created";
    public const string TransferAccepted = "transfer.accepted";
    public const string TransferDeclined = "transfer.declined";
    public const string TransferProgress = "transfer.progress";
    public const string TransferReady = "transfer.ready";
    public const string TransferCompleted = "transfer.completed";
    public const string TransferCancelled = "transfer.cancelled";
    public const string TransferFailed = "transfer.failed";
    public const string Pong = "pong";
    public const string Error = "error";
}
