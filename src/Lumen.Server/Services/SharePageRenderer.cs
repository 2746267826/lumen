using System.Net;
using System.Text;
using Lumen.Server.Api;
using Lumen.Server.Domain;
using Lumen.Server.Infrastructure;
using Lumen.Shared;

namespace Lumen.Server.Services;

public sealed record SharePageResult(string Html, bool NotFound, bool Gone);

public static class SharePageRenderer
{
    public static SharePageResult Render(
        Share? share,
        HttpContext context,
        CryptoService crypto,
        string? error)
    {
        if (share is null || share.Revoked)
        {
            return new SharePageResult(Shell(
                "分享不存在",
                "这个分享链接不存在，或已被创建者撤销。",
                ""), true, false);
        }

        if (share.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new SharePageResult(Shell(
                "分享已过期",
                "这个分享链接已超过有效期，请联系发送者重新分享。",
                ""), false, true);
        }

        if (ApiSupport.IsDownloadLimitReached(share))
        {
            return new SharePageResult(Shell(
                "下载次数已用完",
                "这个分享已达到设定的下载次数上限。",
                ""), false, false);
        }

        var cookieName = ApiSupport.ShareCookieName(share.Token);
        var authorized = string.IsNullOrWhiteSpace(share.PasswordHash) ||
                         (context.Request.Cookies.TryGetValue(cookieName, out var cookie) &&
                          crypto.VerifyShareCookie(share.Token, share.PasswordHash, cookie));

        if (!authorized)
        {
            var formError = error == "password"
                ? "<div class=\"error\">密码不正确，请重试。</div>"
                : "";
            var form = $"""
                {formError}
                <form method="post" action="/s/{WebUtility.HtmlEncode(share.Token)}" class="unlock">
                  <label for="password">此分享已加密</label>
                  <p>输入发送者提供的密码后查看内容。</p>
                  <div class="row">
                    <input id="password" name="password" type="password" autocomplete="current-password"
                           placeholder="分享密码" maxlength="64" required autofocus />
                    <button type="submit">解锁</button>
                  </div>
                </form>
                """;
            return new SharePageResult(Shell("受保护的分享", form, BuildMeta(share)), false, false);
        }

        var expires = share.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var downloadInfo = share.MaxDownloads > 0
            ? $"{share.DownloadCount} / {share.MaxDownloads} 次下载"
            : "不限下载次数";

        if (share.Kind == TransferKinds.Text)
        {
            var text = WebUtility.HtmlEncode(share.TextContent ?? "");
            var body = $"""
                <div class="file-head">
                  <div class="file-icon text">TXT</div>
                  <div>
                    <p>{FormatBytes(share.Size)} · 文字分享</p>
                  </div>
                </div>
                <pre class="text-content">{text}</pre>
                <div class="actions">
                  <a class="button" href="/api/public/shares/{WebUtility.HtmlEncode(share.Token)}/download">下载为文本</a>
                  <span>有效期至 {expires}</span>
                </div>
                <p class="fine">{downloadInfo} · 由 Lumen 流明互传安全分享</p>
                """;
            return new SharePageResult(Shell(share.Name, body, BuildMeta(share)), false, false);
        }

        var preview = share.Kind == TransferKinds.Image
            ? $"<img class=\"preview\" src=\"/api/public/shares/{WebUtility.HtmlEncode(share.Token)}/preview\" alt=\"图片预览\" />"
            : "";
        var html = $"""
            <div class="file-head">
              <div class="file-icon">{(share.Kind == TransferKinds.Image ? "IMG" : "FILE")}</div>
              <div>
                <p>{FormatBytes(share.Size)} · {WebUtility.HtmlEncode(share.MimeType)}</p>
              </div>
            </div>
            {preview}
            <div class="actions">
              <a class="button" href="/api/public/shares/{WebUtility.HtmlEncode(share.Token)}/download">下载文件</a>
              <span>有效期至 {expires}</span>
            </div>
            <p class="fine">{downloadInfo} · 由 Lumen 流明互传安全分享</p>
            """;
        return new SharePageResult(Shell(share.Name, html, BuildMeta(share)), false, false);
    }

    private static string BuildMeta(Share share) =>
        $"""<meta name="description" content="通过 Lumen 分享 · {WebUtility.HtmlEncode(FormatBytes(share.Size))}" />""";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    private static string Shell(string title, string content, string meta) => $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{WebUtility.HtmlEncode(title)}} · Lumen</title>
          {{meta}}
          <style>
            :root {
              color-scheme: dark;
              --bg: #090b12;
              --panel: rgba(20, 24, 38, .82);
              --line: rgba(255, 255, 255, .09);
              --text: #f5f7ff;
              --muted: #929bb4;
              --accent: #8b7cff;
              --accent2: #48d7c4;
            }
            * { box-sizing: border-box; }
            body {
              margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 28px;
              font-family: ui-sans-serif, -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
              color: var(--text);
              background:
                radial-gradient(900px 500px at 15% 0%, rgba(80, 67, 195, .30), transparent 60%),
                radial-gradient(700px 500px at 95% 100%, rgba(28, 145, 140, .20), transparent 55%),
                var(--bg);
            }
            .card {
              width: min(640px, 100%); padding: 30px; border-radius: 28px;
              background: var(--panel); border: 1px solid var(--line);
              box-shadow: 0 30px 100px rgba(0, 0, 0, .48); backdrop-filter: blur(28px);
            }
            .brand { display: flex; align-items: center; gap: 10px; margin-bottom: 28px; color: var(--muted); font-size: 13px; letter-spacing: .08em; text-transform: uppercase; }
            .logo { width: 30px; height: 30px; border-radius: 10px; display: grid; place-items: center;
              background: linear-gradient(135deg, var(--accent), var(--accent2)); color: #0b0d14; font-weight: 800; letter-spacing: -.05em; }
            h1 { margin: 0 0 10px; font-size: clamp(25px, 6vw, 36px); letter-spacing: -.035em; overflow-wrap: anywhere; }
            h2 { margin: 0 0 6px; font-size: 19px; letter-spacing: -.02em; overflow-wrap: anywhere; }
            p { color: var(--muted); line-height: 1.7; }
            .file-head { display: flex; align-items: center; gap: 15px; margin: 8px 0 24px; }
            .file-icon { flex: 0 0 58px; width: 58px; height: 58px; border-radius: 18px; display: grid; place-items: center;
              background: linear-gradient(145deg, rgba(139,124,255,.28), rgba(72,215,196,.14));
              border: 1px solid rgba(139,124,255,.26); color: #c9c1ff; font-size: 11px; font-weight: 800; letter-spacing: .08em; }
            .file-icon.text { color: #9ff2e6; border-color: rgba(72,215,196,.25); }
            .preview { width: 100%; max-height: 420px; object-fit: contain; border-radius: 18px;
              border: 1px solid var(--line); background: rgba(0,0,0,.25); margin: 8px 0 24px; }
            .text-content { max-height: 360px; overflow: auto; padding: 18px; border-radius: 16px; white-space: pre-wrap;
              overflow-wrap: anywhere; background: rgba(4, 6, 12, .55); border: 1px solid var(--line);
              color: #e3e7f6; font: 14px/1.7 ui-monospace, SFMono-Regular, Consolas, monospace; }
            .actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 14px; margin-top: 24px; }
            .actions span { color: var(--muted); font-size: 12px; }
            .button, button { appearance: none; border: 0; border-radius: 14px; padding: 13px 22px; font: 600 14px/1 inherit;
              color: #0b0d14; background: linear-gradient(135deg, #a99dff, #58e0ce); cursor: pointer; text-decoration: none;
              box-shadow: 0 10px 28px rgba(108, 95, 255, .24); transition: transform .15s ease, box-shadow .15s ease; }
            .button:hover, button:hover { transform: translateY(-1px); box-shadow: 0 14px 34px rgba(108, 95, 255, .34); }
            .unlock { margin-top: 22px; }
            .unlock label { display: block; font-weight: 700; margin-bottom: 7px; }
            .row { display: flex; gap: 10px; flex-wrap: wrap; }
            input { flex: 1 1 200px; min-width: 0; border-radius: 14px; border: 1px solid var(--line);
              background: rgba(4, 6, 12, .56); color: var(--text); padding: 13px 15px; font: inherit; outline: none; }
            input:focus { border-color: rgba(139,124,255,.7); box-shadow: 0 0 0 3px rgba(139,124,255,.12); }
            .error { color: #ff9dab; background: rgba(255,70,99,.08); border: 1px solid rgba(255,70,99,.18);
              border-radius: 12px; padding: 11px 14px; margin-bottom: 14px; font-size: 13px; }
            .fine { font-size: 11px; color: #6f7891; margin: 24px 0 0; }
            @media (max-width: 520px) { .card { padding: 22px; border-radius: 22px; } body { padding: 14px; } }
          </style>
        </head>
        <body>
          <main class="card">
            <div class="brand"><span class="logo">L</span><span>Lumen · 流明互传</span></div>
            <h1>{{WebUtility.HtmlEncode(title)}}</h1>
            {{content}}
          </main>
        </body>
        </html>
        """;
}
