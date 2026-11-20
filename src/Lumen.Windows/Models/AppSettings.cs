using System.Text.Json.Serialization;

namespace Lumen.Windows.Models;

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "http://localhost:8080";
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = Environment.MachineName;
    public string ReceiveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Lumen");
    public int ChunkSize { get; set; } = 1024 * 1024;
    public string ThemeMode { get; set; } = "system";
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public string? ProtectedAuthToken { get; set; }
    public bool ShowDetailsOnTransfer { get; set; }
    public bool MinimizeToTray { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string? AuthToken { get; set; }
}
