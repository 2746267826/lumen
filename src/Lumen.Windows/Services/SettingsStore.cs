using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumen.Shared;
using Lumen.Windows.Infrastructure;
using Lumen.Windows.Models;

namespace Lumen.Windows.Services;

public sealed class SettingsStore : IDisposable
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SettingsStore()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lumen");
        Directory.CreateDirectory(DataDirectory);
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        Settings = Load();
        EnsureDefaults();
    }

    public string DataDirectory { get; }
    public string SettingsPath { get; }
    public AppSettings Settings { get; private set; }

    public void EnsureDefaults()
    {
        if (Settings.DeviceId == Guid.Empty)
        {
            Settings.DeviceId = Guid.NewGuid();
        }

        if (string.IsNullOrWhiteSpace(Settings.DeviceName))
        {
            Settings.DeviceName = Environment.MachineName;
        }

        if (string.IsNullOrWhiteSpace(Settings.ReceiveDirectory))
        {
            Settings.ReceiveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Lumen");
        }

        Settings.ChunkSize = Math.Clamp(
            Settings.ChunkSize,
            64 * 1024,
            LumenProtocol.MaxChunkSize);
        Directory.CreateDirectory(Settings.ReceiveDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "incoming"));
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            Settings.ProtectedAuthToken = ProtectToken(Settings.AuthToken);
            var json = JsonSerializer.SerializeToUtf8Bytes(Settings, LumenJson.PrettyOptions);
            await FileUtilities.AtomicWriteAsync(SettingsPath, json, ct);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath),
                LumenJson.Options) ?? new AppSettings();
            settings.AuthToken = UnprotectToken(settings.ProtectedAuthToken);
            if (settings.TokenExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            {
                settings.AuthToken = null;
                settings.ProtectedAuthToken = null;
                settings.TokenExpiresAt = null;
            }

            return settings;
        }
        catch
        {
            var corruptPath = SettingsPath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                File.Move(SettingsPath, corruptPath);
            }
            catch
            {
            }

            return new AppSettings();
        }
    }

    private static string? ProtectToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token),
                null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return null;
        }
    }

    private static string? UnprotectToken(string? protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedToken),
                null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _saveLock.Dispose();
}
