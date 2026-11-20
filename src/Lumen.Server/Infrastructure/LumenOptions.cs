namespace Lumen.Server.Infrastructure;

public sealed class LumenOptions
{
    public string DataDir { get; set; } = "./data";
    public string PublicUrl { get; set; } = "";
    public bool AllowRegistration { get; set; } = true;
    public int SessionDays { get; set; } = 90;
    public int RetentionDays { get; set; } = 14;
    public long MaxShareBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public long MaxTransferBytes { get; set; } = 200L * 1024 * 1024 * 1024;
    public int ChunkMaxBytes { get; set; } = 8 * 1024 * 1024;
    public string ShareSecretFile { get; set; } = "share-secret.key";
}

public sealed class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public static class ApiErrors
{
    public static ApiException BadRequest(string code, string message) => new(400, code, message);
    public static ApiException Unauthorized(string message = "登录已失效，请重新登录。") => new(401, "unauthorized", message);
    public static ApiException Forbidden(string message = "没有权限执行此操作。") => new(403, "forbidden", message);
    public static ApiException NotFound(string message = "对象不存在。") => new(404, "not_found", message);
    public static ApiException Conflict(string code, string message) => new(409, code, message);
    public static ApiException TooLarge(string message = "内容超过服务器限制。") => new(413, "too_large", message);
}
