using Lumen.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace Lumen.Server.Services;

public sealed class StorageService
{
    private readonly string _transfersRoot;
    private readonly string _sharesRoot;

    public StorageService(IOptions<LumenOptions> options, IWebHostEnvironment env)
    {
        var configured = options.Value.DataDir;
        Root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(env.ContentRootPath, configured));
        _transfersRoot = Path.Combine(Root, "transfers");
        _sharesRoot = Path.Combine(Root, "shares");
        Directory.CreateDirectory(_transfersRoot);
        Directory.CreateDirectory(_sharesRoot);
    }

    public string Root { get; }

    public string GetTransferDirectory(Guid transferId) =>
        Path.Combine(_transfersRoot, transferId.ToString("N"));

    public string GetChunkPath(Guid transferId, int index) =>
        Path.Combine(GetTransferDirectory(transferId), $"{index:D8}.chunk");

    public string GetSharePath(Guid shareId) =>
        Path.Combine(_sharesRoot, $"{shareId:N}.bin");

    public void DeleteTransfer(Guid transferId)
    {
        var directory = GetTransferDirectory(transferId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    public void DeleteShare(Guid shareId, string? storagePath)
    {
        if (!string.IsNullOrWhiteSpace(storagePath) && File.Exists(storagePath))
        {
            File.Delete(storagePath);
        }
    }

    public static string NormalizeRemoteIp(System.Net.IPAddress? ip)
    {
        if (ip is null)
        {
            return "";
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return ip.ToString();
    }

    public static string GetFamily(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? "" :
        ip.Contains(':', StringComparison.Ordinal) ? "IPv6" : "IPv4";
}
