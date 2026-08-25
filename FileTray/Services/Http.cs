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
        await DownloadToFileAsync(url, savePath, null).ConfigureAwait(false);
    }

    /// <summary>
    /// 下载到文件并周期性上报进度(约每 100ms 一次,读不到 Content-Length 时 total=-1、percent=-1)。
    /// 进度回调在线程池线程触发,调用方自行切换 UI 线程。
    /// </summary>
    public static async Task DownloadToFileAsync(string url, string savePath, IProgress<(int Percent, long Received, long Total)>? progress)
    {
        using var response = await LongClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        if (progress is null)
        {
            await source.CopyToAsync(stream).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[64 * 1024];
        long received = 0;
        var lastReport = DateTime.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            received += read;

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 100)
            {
                lastReport = now;
                progress.Report((total > 0 ? (int)(received * 100 / total) : -1, received, total));
            }
        }
        progress.Report((total > 0 ? 100 : -1, Math.Max(received, 0), total));
    }
}
