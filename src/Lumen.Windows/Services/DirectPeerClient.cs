using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumen.Shared;
using Lumen.Windows.Models;

namespace Lumen.Windows.Services;

public sealed record DirectEndpoint(
    string BaseUrl,
    string Address,
    string Family,
    DirectPingResponse Ping);

public sealed class DirectPeerClient : IDisposable
{
    private readonly HttpClient _http;

    public DirectPeerClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(900),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lumen-Windows/1.0");
    }

    public async Task<DirectEndpoint?> ProbeAsync(
        PeerDevice peer,
        CancellationToken ct = default)
    {
        if (!peer.LanOnline || peer.LanPort <= 0)
        {
            return null;
        }

        var candidates = peer.LanAddresses
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Contains(':', StringComparison.Ordinal) ? 1 : 0)
            .Select(address => NetworkUtilities.BuildHttpBase(address, peer.LanPort))
            .ToList();
        foreach (var baseUrl in candidates)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(850));
            try
            {
                using var response = await _http.GetAsync(
                    $"{baseUrl}{LumenRoutes.Ping}",
                    timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var ping = await JsonSerializer.DeserializeAsync<DirectPingResponse>(
                    await response.Content.ReadAsStreamAsync(timeout.Token),
                    LumenJson.Options,
                    timeout.Token);
                if (ping is null || ping.DeviceId != peer.Id)
                {
                    continue;
                }

                var uri = new Uri(baseUrl);
                return new DirectEndpoint(
                    baseUrl,
                    uri.Host,
                    NetworkUtilities.FamilyOf(uri.Host),
                    ping);
            }
            catch
            {
            }
        }

        return null;
    }

    public async Task<DirectOfferResponse> SendOfferAsync(
        DirectEndpoint endpoint,
        DirectOfferRequest offer,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint.BaseUrl}{LumenRoutes.Offers}")
        {
            Content = JsonContent(offer)
        };
        using var response = await _http.SendAsync(request, timeout.Token);
        return await ReadAsync<DirectOfferResponse>(response, timeout.Token);
    }

    public async Task<DirectOfferStatus> GetOfferStatusAsync(
        DirectEndpoint endpoint,
        Guid transferId,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _http.GetAsync(
            $"{endpoint.BaseUrl}{LumenRoutes.Offers}/{transferId}",
            timeout.Token);
        return await ReadAsync<DirectOfferStatus>(response, timeout.Token);
    }

    public async Task<ChunkUploadResultDto> UploadChunkAsync(
        DirectEndpoint endpoint,
        Guid transferId,
        int index,
        string uploadToken,
        byte[] data,
        string sha256,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{endpoint.BaseUrl}/lumen/v1/transfers/{transferId}/chunks/{index}");
        request.Headers.Add(LumenProtocol.UploadTokenHeader, uploadToken);
        request.Headers.Add("X-Lumen-Chunk-Sha256", sha256);
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = content;
        using var response = await _http.SendAsync(request, timeout.Token);
        return await ReadAsync<ChunkUploadResultDto>(response, timeout.Token);
    }

    public async Task CompleteAsync(
        DirectEndpoint endpoint,
        Guid transferId,
        string uploadToken,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint.BaseUrl}/lumen/v1/transfers/{transferId}/complete");
        request.Headers.Add(LumenProtocol.UploadTokenHeader, uploadToken);
        using var response = await _http.SendAsync(request, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
    }

    public async Task CancelAsync(
        DirectEndpoint endpoint,
        Guid transferId,
        string uploadToken,
        CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{endpoint.BaseUrl}/lumen/v1/transfers/{transferId}/cancel");
            request.Headers.Add(LumenProtocol.UploadTokenHeader, uploadToken);
            using var response = await _http.SendAsync(request, timeout.Token);
        }
        catch
        {
        }
    }

    private static ByteArrayContent JsonContent(object value)
    {
        var content = new ByteArrayContent(
            JsonSerializer.SerializeToUtf8Bytes(value, LumenJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, LumenJson.Options, ct)
               ?? throw new ApiClientException(
                   (int)response.StatusCode,
                   "invalid_response",
                   "局域网设备返回了空响应。");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = $"局域网传输失败（HTTP {(int)response.StatusCode}）。";
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var error = JsonSerializer.Deserialize<ApiError>(body, LumenJson.Options);
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                message = error.Message;
            }
        }
        catch
        {
        }

        throw new ApiClientException((int)response.StatusCode, "lan_error", message);
    }

    public void Dispose() => _http.Dispose();
}
