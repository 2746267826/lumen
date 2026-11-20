using Lumen.Shared;
using Lumen.Windows.Infrastructure;

namespace Lumen.Windows.Models;

public sealed class ShareItem : ObservableObject
{
    public ShareItem(ShareDto dto)
    {
        Id = dto.Id;
        Url = dto.Url;
        Name = dto.Name;
        Kind = dto.Kind;
        MimeType = dto.MimeType;
        Size = dto.Size;
        PasswordProtected = dto.PasswordProtected;
        ExpiresAt = dto.ExpiresAt;
        MaxDownloads = dto.MaxDownloads;
        DownloadCount = dto.DownloadCount;
        CreatedAt = dto.CreatedAt;
    }

    public Guid Id { get; }
    public string Url { get; }
    public string Name { get; }
    public string Kind { get; }
    public string MimeType { get; }
    public long Size { get; }
    public bool PasswordProtected { get; }
    public DateTimeOffset ExpiresAt { get; }
    public int MaxDownloads { get; }
    public int DownloadCount { get; }
    public DateTimeOffset CreatedAt { get; }

    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;
    public string SizeLabel => Kind == TransferKinds.Text ? "文字" : Formatters.Bytes(Size);
    public string KindGlyph => Kind switch
    {
        TransferKinds.Text => "\uE8A5",
        TransferKinds.Image => "\uEB9F",
        _ => "\uE7C3"
    };
    public string ProtectionLabel => PasswordProtected ? "密码保护" : "公开链接";
    public string ProtectionGlyph => PasswordProtected ? "\uE72E" : "\uE785";
    public string DownloadLabel => MaxDownloads == 0
        ? $"{DownloadCount} 次下载 · 不限"
        : $"{DownloadCount}/{MaxDownloads} 次下载";
    public string ExpiryLabel => IsExpired
        ? "已过期"
        : $"有效至 {ExpiresAt.ToLocalTime():MM-dd HH:mm}";
    public string StatusLabel => IsExpired ? "已过期" : "可访问";
    public string StatusColor => IsExpired ? "#FF8795" : "#55DCA8";
    public string CreatedLabel => Formatters.RelativeTime(CreatedAt);
}
