using System.Security.Cryptography;

namespace Lumen.Windows.Infrastructure;

public static class FileUtilities
{
    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".json"] = "application/json",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".mov"] = "video/quicktime",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".apk"] = "application/vnd.android.package-archive",
        [".exe"] = "application/vnd.microsoft.portable-executable",
        [".iso"] = "application/x-iso9660-image"
    };

    public static string GetMimeType(string path)
    {
        var extension = Path.GetExtension(path);
        return MimeMap.TryGetValue(extension, out var mime)
            ? mime
            : "application/octet-stream";
    }

    public static bool IsImage(string? mime, string? path = null) =>
        (mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (path is not null && MimeMap.TryGetValue(Path.GetExtension(path), out var detected) &&
         detected.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ResolveIncomingPath(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var safeName = string.IsNullOrWhiteSpace(name) ? "Lumen 文件" : name;
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(c, '_');
        }

        var candidate = Path.Combine(directory, safeName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
    }

    public static async Task AtomicWriteAsync(string path, byte[] content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, path, overwrite: true);
    }
}
