namespace Lumen.Server.Domain;

public sealed class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string? Model { get; set; }
    public string? AppVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? LastIp { get; set; }
    public string? LastIpFamily { get; set; }
}

public sealed class Transfer
{
    public Guid Id { get; set; }
    public Guid SenderDeviceId { get; set; }
    public Device SenderDevice { get; set; } = null!;
    public Guid RecipientDeviceId { get; set; }
    public Device RecipientDevice { get; set; } = null!;
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string MimeType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public int ChunkSize { get; set; }
    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }
    public long TransferredBytes { get; set; }
    public string Status { get; set; } = "pending";
    public string Route { get; set; } = "relay";
    public string? SenderAddress { get; set; }
    public string? RecipientAddress { get; set; }
    public string? TextContent { get; set; }
    public string? Error { get; set; }
    public double? AverageSpeedBps { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<TransferChunk> Chunks { get; set; } = [];
    public List<TransferEvent> Events { get; set; } = [];
}

public sealed class TransferChunk
{
    public Guid TransferId { get; set; }
    public Transfer Transfer { get; set; } = null!;
    public int Index { get; set; }
    public int Size { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
}

public sealed class TransferEvent
{
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public Transfer Transfer { get; set; } = null!;
    public string Type { get; set; } = "";
    public string? Message { get; set; }
    public string? Address { get; set; }
    public string? IpFamily { get; set; }
    public long? Bytes { get; set; }
    public double? SpeedBps { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class Share
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "file";
    public string MimeType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string? StoragePath { get; set; }
    public string? TextContent { get; set; }
    public string? PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int MaxDownloads { get; set; }
    public int DownloadCount { get; set; }
    public bool Revoked { get; set; }
}
