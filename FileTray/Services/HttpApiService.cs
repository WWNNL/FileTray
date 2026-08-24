using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileTray.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FileTray.Services;

public sealed class TransferSession
{
    public string Id { get; init; } = "";
    public string SenderAlias { get; init; } = "";
    public string SenderIp { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public ConcurrentDictionary<string, (string Token, FileMetaDto Meta)> Files { get; } = new();
}

/// <summary>
/// 内嵌 HTTP 服务(Kestrel):
/// - LocalSend v2 兼容 API:info / register / prepare-upload / upload / cancel
/// - FileTray 分布式 API:room/sync(状态交换)、ping(延迟检测)、file(托盘文件下载)
/// MVP 阶段仅使用 HTTP(不启用 TLS),收到传输请求一律自动接受。
/// </summary>
public sealed class HttpApiService : IDisposable
{
    private readonly DiscoveryService _discovery;
    private readonly RoomService _room;
    private readonly Func<DeviceInfoDto> _selfInfo;
    private readonly ConcurrentDictionary<string, TransferSession> _sessions = new();
    private WebApplication? _app;
    private Timer? _sessionSweeper;

    public int Port { get; private set; }

    /// <summary>(发送者别名, 发送者 IP, 文本内容)</summary>
    public event Action<string, string, string>? TextReceived;

    /// <summary>(发送者别名, 文件名, 大小, 保存路径)</summary>
    public event Action<string, string, long, string>? FileReceived;

    public HttpApiService(DiscoveryService discovery, RoomService room, Func<DeviceInfoDto> selfInfo)
    {
        _discovery = discovery;
        _room = room;
        _selfInfo = selfInfo;
    }

    public async Task StartAsync(int preferredPort)
    {
        Exception? lastError = null;
        for (var port = preferredPort; port < preferredPort + 20; port++)
        {
            WebApplication app;
            try
            {
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(port);
                    options.Limits.MaxRequestBodySize = null; // 局域网内不限制大小
                });
                app = builder.Build();
            }
            catch (Exception ex)
            {
                lastError = ex;
                continue;
            }

            try
            {
                MapRoutes(app);
                await app.StartAsync().ConfigureAwait(false);
                _app = app;
                Port = port;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await app.DisposeAsync().ConfigureAwait(false);
                Log.Warn($"HTTP 端口 {port} 不可用: {ex.Message}");
            }
        }

        if (_app is null)
            throw new InvalidOperationException($"无法绑定 HTTP 端口 {preferredPort}~{preferredPort + 19}: {lastError?.Message}");

        _sessionSweeper = new Timer(_ => SweepSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Log.Info($"HTTP 服务已启动: 端口 {Port}");
    }

    private void MapRoutes(WebApplication app)
    {
        // ================= LocalSend v2 兼容 =================

        app.MapGet("/api/localsend/v2/info", () => Results.Json(_selfInfo(), Http.Json));

        app.MapPost("/api/localsend/v2/register", (DeviceInfoDto body, HttpContext ctx) =>
        {
            var remote = ctx.Connection.RemoteIpAddress ?? IPAddress.Loopback;
            _discovery.Record(body, System.Net.IPAddress.Parse(NetUtil.NormalizeIp(remote.ToString())));
            return Results.Json(_selfInfo(), Http.Json);
        });

        app.MapPost("/api/localsend/v2/prepare-upload", (PrepareUploadRequestDto body, HttpContext ctx) =>
        {
            var session = new TransferSession
            {
                Id = Guid.NewGuid().ToString("N"),
                SenderAlias = body.Info?.Alias ?? "未知设备",
                SenderIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
                CreatedUtc = DateTime.UtcNow,
            };
            foreach (var (fileId, meta) in body.Files ?? new Dictionary<string, FileMetaDto>())
            {
                session.Files[fileId] = (Guid.NewGuid().ToString("N"), meta);
            }
            _sessions[session.Id] = session;

            // MVP: 一律自动接受
            return Results.Json(new PrepareUploadResponseDto
            {
                SessionId = session.Id,
                Files = session.Files.ToDictionary(kv => kv.Key, kv => kv.Value.Token),
            }, Http.Json);        });

        app.MapPost("/api/localsend/v2/upload", async (HttpRequest request) =>
        {
            var sessionId = request.Query["sessionId"].ToString();
            var fileId = request.Query["fileId"].ToString();
            var token = request.Query["token"].ToString();
            if (!_sessions.TryGetValue(sessionId, out var session)) return Results.NotFound();
            if (!session.Files.TryRemove(fileId, out var entry) || entry.Token != token) return Results.Forbid();

            var meta = entry.Meta;
            if (meta.FileType == "text/plain" && meta.Size <= 1024 * 1024)
            {
                using var buffer = new MemoryStream();
                await request.Body.CopyToAsync(buffer).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(buffer.ToArray());
                Log.Info($"收到文本 来自 {session.SenderAlias}: {(text.Length > 100 ? text[..100] + "…" : text)}");
                TextReceived?.Invoke(session.SenderAlias, session.SenderIp, text);
            }
            else
            {
                var savedPath = await SaveStreamAsync(request.Body, meta.FileName).ConfigureAwait(false);
                Log.Info($"收到文件 来自 {session.SenderAlias}: {meta.FileName} ({meta.Size} 字节) → {savedPath}");
                FileReceived?.Invoke(session.SenderAlias, meta.FileName, meta.Size, savedPath);
            }

            if (session.Files.IsEmpty) _sessions.TryRemove(session.Id, out _);
            return Results.NoContent();
        });

        app.MapPost("/api/localsend/v2/cancel", (HttpRequest request) =>
        {
            var sessionId = request.Query["sessionId"].ToString();
            _sessions.TryRemove(sessionId, out _);
            return Results.NoContent();
        });

        // ================= FileTray 分布式房间 =================

        // 延迟检测端点:立即返回,客户端测 RTT
        app.MapGet("/api/filetray/v1/ping", () => Results.Ok("pong"));

        // 节点间状态交换:合并对方状态,返回自己的完整状态(条目 + 墓碑)
        app.MapPost("/api/filetray/v1/room/sync", (RoomSyncDto body) =>
        {
            var response = _room.MergeSync(body);
            return response is null ? Results.NotFound() : Results.Json(response, Http.Json);
        });

        // 本地房间状态(调试/联调用)
        app.MapGet("/api/filetray/v1/room/{code}", (string code) =>
        {
            var state = _room.GetLocalState(code);
            return state is null ? Results.NotFound() : Results.Json(state, Http.Json);
        });

        // 只允许下载本机放入该房间托盘的文件
        app.MapGet("/api/filetray/v1/file", (string path, string code) =>
        {
            var resolved = _room.ValidateOwnFile(path, code);
            return resolved is null
                ? Results.NotFound()
                : Results.File(resolved, "application/octet-stream", Path.GetFileName(resolved));
        });
    }

    private static async Task<string> SaveStreamAsync(Stream body, string fileName)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FileTray");
        Directory.CreateDirectory(directory);

        var safeName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (safeName.Length == 0) safeName = "file";

        var target = Path.Combine(directory, safeName);
        var counter = 1;
        while (File.Exists(target))
        {
            target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(safeName)} ({counter++}){Path.GetExtension(safeName)}");
        }

        await using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        await body.CopyToAsync(stream).ConfigureAwait(false);
        return target;
    }

    private void SweepSessions()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var (id, session) in _sessions)
            {
                if (session.CreatedUtc < cutoff) _sessions.TryRemove(id, out _);
            }
        }
        catch
        {
            // 忽略
        }
    }

    public async Task StopAsync()
    {
        _sessionSweeper?.Dispose();
        if (_app is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _app.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch { }
            try { await _app.DisposeAsync().ConfigureAwait(false); } catch { }
            _app = null;
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
