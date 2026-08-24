using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileTray.Services;

/// <summary>
/// 共享的 HTTP 客户端与 JSON 选项。
/// LocalSend 默认使用 HTTPS + 运行时自签证书,局域网内按指纹确认的信任模型,MVP 直接放行所有证书。
/// </summary>
public static class Http
{
    private static readonly HttpClientHandler InsecureHandler = new()
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    };

    public static readonly HttpClient Client = new(InsecureHandler) { Timeout = TimeSpan.FromSeconds(30) };

    public static readonly HttpClient LongClient = new(InsecureHandler) { Timeout = TimeSpan.FromMinutes(10) };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<string?> GetStringAsync(string url, int timeoutMs, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var response = await Client.GetAsync(url, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    public static async Task<T?> GetJsonAsync<T>(string url, int timeoutMs, CancellationToken ct = default) where T : class
    {
        var text = await GetStringAsync(url, timeoutMs, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(text) ? null : JsonSerializer.Deserialize<T>(text, Json);
    }

    public static async Task<T?> PostJsonAsync<T>(string url, object body, int timeoutMs, CancellationToken ct = default) where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync(url, content, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return string.IsNullOrEmpty(text) ? null : JsonSerializer.Deserialize<T>(text, Json);
    }

    public static async Task PostJsonAsync(string url, object body, int timeoutMs, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync(url, content, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public static async Task PostBytesAsync(string url, byte[] body, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var content = new ByteArrayContent(body);
        using var response = await Client.PostAsync(url, content, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public static async Task DownloadToFileAsync(string url, string savePath)
    {
        using var response = await LongClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(stream).ConfigureAwait(false);
    }
}
