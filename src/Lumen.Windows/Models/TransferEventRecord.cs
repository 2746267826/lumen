using Lumen.Windows.Infrastructure;

namespace Lumen.Windows.Models;

public sealed class TransferEventRecord
{
    public DateTimeOffset At { get; set; }
    public string Type { get; set; } = "";
    public string? Message { get; set; }
    public string? Address { get; set; }
    public string? IpFamily { get; set; }
    public long? Bytes { get; set; }
    public double? SpeedBps { get; set; }

    public string TimeLabel => Formatters.FullTime(At);
    public string TypeLabel => Type switch
    {
        "created" => "创建",
        "accepted" => "接受",
        "declined" => "拒绝",
        "chunk.uploaded" => "上传分块",
        "chunk.downloaded" => "下载分块",
        "ready" => "就绪",
        "completed" => "完成",
        "cancelled" => "取消",
        "failed" => "失败",
        "route.selected" => "选路",
        "probe" => "探测",
        "merged" => "合并",
        _ => Type
    };

    public string Detail => string.Join(
        " · ",
        new[]
        {
            Message,
            Bytes is { } bytes ? Formatters.Bytes(bytes) : null,
            SpeedBps is { } speed ? Formatters.Speed(speed) : null,
            IpFamily,
            Address
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
}
