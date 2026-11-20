using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Lumen.Shared;
using Lumen.Windows.Infrastructure;
using Lumen.Windows.Models;

namespace Lumen.Windows.Services;

public sealed class ApiClientException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record RelayChunkDownload(int Size, string? Sha256);

public sealed class ApiClient : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;

    public ApiClient(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lumen-Windows/1.0");
    }

    public string ServerUrl => NormalizeServerUrl(_settings.ServerUrl);

    public static string NormalizeServerUrl(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "http://localhost:8080";
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }

    public async Task<HealthResponse> HealthAsync(string? serverUrl = null, CancellationToken ct = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl ?? _settings.ServerUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/health");
        using var response = await _http.SendAsync(request, ct);
        return await ReadAsync<HealthResponse>(response, ct);
    }

    public Task<AuthResponse> RegisterAsync(
        string username,
        string password,
        string displayName,
        CancellationToken ct = default) =>
        SendJsonAsync<AuthResponse>(
            HttpMethod.Post,
            "/api/v1/auth/register",
            new RegisterRequest(username, password, displayName),
            authenticated: false,
            ct);

    public Task<AuthResponse> LoginAsync(
        string username,
        string password,
        CancellationToken ct = default) =>
        SendJsonAsync<AuthResponse>(
            HttpMethod.Post,
            "/api/v1/auth/login",
            new LoginRequest(username, password),
            authenticated: false,
            ct);

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "/api/v1/auth/logout");
            using var response = await _http.SendAsync(request, ct);
        }
        catch
        {
        }
    }

    public Task<UserDto> MeAsync(CancellationToken ct = default) =>
        SendJsonAsync<UserDto>(HttpMethod.Get, "/api/v1/me", null, true, ct);

    public Task<DeviceRegistrationResponse> RegisterDeviceAsync(
        string deviceName,
        string? model = null,
        CancellationToken ct = default) =>
        SendJsonAsync<DeviceRegistrationResponse>(
            HttpMethod.Post,
            "/api/v1/devices",
            new DeviceRegisterRequest(
                _settings.DeviceId,
                deviceName,
                "windows",
                model,
                "1.0.0"),
            true,
            ct);

    public Task<List<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        SendJsonAsync<List<DeviceDto>>(HttpMethod.Get, "/api/v1/devices", null, true, ct);

    public Task<TransferDto> CreateTransferAsync(
        TransferCreateRequest request,
        CancellationToken ct = default) =>
        SendJsonAsync<TransferDto>(HttpMethod.Post, "/api/v1/transfers", request, true, ct);

    public Task<TransferDetailsDto> GetTransferAsync(
        Guid transferId,
        CancellationToken ct = default) =>
        SendJsonAsync<TransferDetailsDto>(
            HttpMethod.Get,
            $"/api/v1/transfers/{transferId}",
            null,
            true,
            ct);

    public Task<TransferDto> AcceptTransferAsync(Guid transferId, CancellationToken ct = default) =>
        SendJsonAsync<TransferDto>(
            HttpMethod.Post,
            $"/api/v1/transfers/{transferId}/accept",
            null,
            true,
            ct);

    public Task<TransferDto> DeclineTransferAsync(Guid transferId, CancellationToken ct = default) =>
        SendJsonAsync<TransferDto>(
            HttpMethod.Post,
            $"/api/v1/transfers/{transferId}/decline",
            null,
            true,
            ct);

    public Task<TransferDto> ReadyTransferAsync(Guid transferId, CancellationToken ct = default) =>
        SendJsonAsync<TransferDto>(
            HttpMethod.Post,
            $"/api/v1/transfers/{transferId}/ready",
            null,
            true,
            ct);

    public Task<TransferDto> ReceivedTransferAsync(Guid transferId, CancellationToken ct = default) =>
        SendJsonAsync<TransferDto>(
            HttpMethod.Post,
            $"/api/v1/transfers/{transferId}/received",
            null,
            true,
            ct);

    public Task<TransferDto> CancelTransferAsync(Guid transferId, CancellationToken ct = default) =>
        SendJsonAsync<TransferDto>(
            HttpMethod.Post,
            $"/api/v1/transfers/{transferId}/cancel",
            null,
            true,
            ct);

    public async Task<ChunkUploadResultDto> UploadChunkAsync(
        Guid transferId,
        int index,
        byte[] data,
        string sha256,
        CancellationToken ct = default)
    {
        using var request = CreateRequest(
            HttpMethod.Put,
            $"/api/v1/transfers/{transferId}/chunks/{index}");
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.Add("X-Lumen-Chunk-Sha256", sha256);
        request.Content = content;
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        return await ReadAsync<ChunkUploadResultDto>(response, ct);
    }

    public async Task<RelayChunkDownload> DownloadChunkAsync(
        Guid transferId,
        int index,
        Stream target,
        CancellationToken ct = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"/api/v1/transfers/{transferId}/chunks/{index}");
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        await EnsureSuccessAsync(response, ct);
        var shaHeader = response.Headers.TryGetValues("X-Lumen-Chunk-Sha256", out var values)
            ? values.FirstOrDefault()
            : null;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await source.CopyToAsync(target, 128 * 1024, ct);
        return new RelayChunkDownload(
            (int)(response.Content.Headers.ContentLength ?? 0),
            shaHeader);
    }

    public Task<TransferChunksDto> GetChunksAsync(Guid transferId, CancellationToken ct = default) =>
        SendJsonAsync<TransferChunksDto>(
            HttpMethod.Get,
            $"/api/v1/transfers/{transferId}/chunks",
            null,
            true,
            ct);

    public async Task<ShareDto> CreateFileShareAsync(
        string path,
        string? displayName,
        int expiresInHours,
        string? password,
        int maxDownloads,
        CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(path)
            : displayName.Trim();
        var mime = FileUtilities.GetMimeType(path);
        var query =
            $"?name={Uri.EscapeDataString(name)}" +
            $"&mime={Uri.EscapeDataString(mime)}" +
            $"&expiresInHours={expiresInHours}" +
            $"&maxDownloads={maxDownloads}";
        using var request = CreateRequest(HttpMethod.Post, "/api/v1/shares/file" + query);
        if (!string.IsNullOrWhiteSpace(password))
        {
            request.Headers.Add(LumenProtocol.SharePasswordHeader, password);
        }

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(mime.Split(';')[0].Trim());
        content.Headers.ContentLength = stream.Length;
        request.Content = content;
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        return await ReadAsync<ShareDto>(response, ct);
    }

    public Task<ShareDto> CreateTextShareAsync(
        string name,
        string text,
        int expiresInHours,
        string? password,
        int maxDownloads,
        CancellationToken ct = default) =>
        SendJsonAsync<ShareDto>(
            HttpMethod.Post,
            "/api/v1/shares/text",
            new ShareTextRequest(name, text, expiresInHours, password, maxDownloads),
            true,
            ct);

    public Task<List<ShareDto>> GetSharesAsync(CancellationToken ct = default) =>
        SendJsonAsync<List<ShareDto>>(HttpMethod.Get, "/api/v1/shares", null, true, ct);

    public async Task RevokeShareAsync(Guid id, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"/api/v1/shares/{id}");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken ct)
    {
        return SendJsonCoreAsync<T>(method, path, body, authenticated, ct);
    }

    private async Task<T> SendJsonCoreAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken ct)
    {
        using var request = CreateRequest(method, path, authenticated);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(body, LumenJson.Options));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        return await ReadAsync<T>(response, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, $"{ServerUrl}{path}");
        if (authenticated)
        {
            if (string.IsNullOrWhiteSpace(_settings.AuthToken))
            {
                throw new ApiClientException(401, "not_logged_in", "请先在设置中登录服务器。");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _settings.AuthToken);
            request.Headers.Add(LumenProtocol.DeviceHeader, _settings.DeviceId.ToString());
        }

        return request;
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return default!;
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, LumenJson.Options, ct);
        return value ?? throw new ApiClientException(
            (int)response.StatusCode,
            "invalid_response",
            "服务器返回了空响应。");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = $"服务器请求失败（HTTP {(int)response.StatusCode}）。";
        var code = "http_error";
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var error = JsonSerializer.Deserialize<ApiError>(body, LumenJson.Options);
            if (error is not null && !string.IsNullOrWhiteSpace(error.Message))
            {
                code = error.Code;
                message = error.Message;
            }
        }
        catch
        {
        }

        throw new ApiClientException((int)response.StatusCode, code, message);
    }

    public void Dispose() => _http.Dispose();
}
