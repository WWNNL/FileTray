using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FileTray.Models;

namespace FileTray.Services;

/// <summary>
/// 局域网房间:房主权威模式。
/// 创建者生成 8 位房间码并持有权威的成员表 + 托盘列表;
/// 成员的加入/放入文件/删除操作都提交给房主,房主向所有成员全量推送最新状态,
/// 以此保证"传入和删除操作完全同步"。房主每 5 秒心跳广播一次,
/// 成员 15 秒收不到更新即认为房主失联并自动退出房间。
/// </summary>
public sealed class RoomService : IDisposable
{
    private readonly object _sync = new();
    private readonly SettingsService _settings;
    private readonly DiscoveryService _discovery;
    private readonly Func<int> _portProvider;

    private RoomRole _role;
    private string? _code;
    private string? _hostBaseUrl;
    private RoomStateDto? _state;
    private DateTime _lastHostUpdateUtc;
    private Timer? _heartbeatTimer;
    private Timer? _watchdogTimer;
    private readonly Dictionary<string, int> _missCounts = new();

    public event Action? RoomStateChanged;
    public event Action<string>? RoomClosed;

    public RoomRole Role
    {
        get { lock (_sync) return _role; }
    }

    public bool IsInRoom => Role != RoomRole.None;

    public string? Code
    {
        get { lock (_sync) return _code; }
    }

    public RoomStateDto? State
    {
        get { lock (_sync) return _state is null ? null : CloneState(_state); }
    }

    public RoomService(SettingsService settings, DiscoveryService discovery, Func<int> portProvider)
    {
        _settings = settings;
        _discovery = discovery;
        _portProvider = portProvider;
    }

    private MemberDto SelfMember(string? ip = null) => new()
    {
        Fingerprint = _settings.Fingerprint,
        Alias = _settings.Alias,
        Ip = ip ?? LocalIpHelper.GetBestLocalIp(),
        Port = _portProvider(),
    };

    /// <summary>生成 8 位随机房间码(大写字母 + 数字)。</summary>
    public static string GenerateRoomCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(8);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    // ============================ 房主侧 ============================

    public void CreateRoom(string? fixedCode = null)
    {
        lock (_sync)
        {
            if (_role != RoomRole.None) return;
            _role = RoomRole.Host;
            _code = fixedCode ?? GenerateRoomCode();
            _state = new RoomStateDto
            {
                Code = _code,
                Closed = false,
                HostFingerprint = _settings.Fingerprint,
                Members = new List<MemberDto> { SelfMember() },
                Tray = new List<TrayItemDto>(),
            };
            _heartbeatTimer ??= new Timer(_ => HostHeartbeat(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        Log.Info($"房间已创建: {_code} (我是房主)");
        RoomStateChanged?.Invoke();
    }

    public bool IsHosting(string code)
    {
        lock (_sync) return _role == RoomRole.Host && _code == code;
    }

    public RoomStateDto? Snapshot()
    {
        lock (_sync) return _state is null ? null : CloneState(_state);
    }

    public RoomStateDto? TryHostJoin(RoomJoinRequestDto request, IPAddress? remoteIp)
    {
        RoomStateDto? snapshot;
        string alias;
        string ip;
        lock (_sync)
        {
            if (_role != RoomRole.Host || _code != request.Code || _state is null) return null;
            var member = request.Member ?? new MemberDto();
            if (string.IsNullOrEmpty(member.Fingerprint)) return null;

            member.Ip = NetUtil.NormalizeIp((remoteIp ?? IPAddress.Loopback).ToString());
            alias = member.Alias;
            ip = member.Ip;

            var existing = _state.Members.FirstOrDefault(m => m.Fingerprint == member.Fingerprint);
            if (existing is null) _state.Members.Add(member);
            else
            {
                existing.Alias = member.Alias;
                existing.Ip = member.Ip;
                existing.Port = member.Port;
            }

            // 重新加入视为恢复在线,清除失联计数
            _missCounts.Remove(member.Fingerprint);

            // 用加入方实际连到的地址修正自己在成员表里的 IP(处理多网卡场景)
            if (!string.IsNullOrEmpty(request.SeenHostIp))
            {
                var seen = NetUtil.NormalizeIp(request.SeenHostIp);
                var self = _state.Members.FirstOrDefault(m => m.Fingerprint == _settings.Fingerprint);
                if (self != null) self.Ip = seen;
            }

            snapshot = CloneState(_state);
        }

        Log.Info($"成员加入房间 {request.Code}: {alias} ({ip}),共 {snapshot!.Members.Count} 人");
        _ = BroadcastAsync(snapshot);
        return snapshot;
    }

    public RoomStateDto? HostAddTray(TrayAddRequestDto request)
    {
        RoomStateDto? snapshot;
        string fileName;
        string ownerAlias;
        lock (_sync)
        {
            if (_role != RoomRole.Host || _code != request.Code || _state is null || request.Item is null) return null;
            var item = request.Item;
            item.Id = Guid.NewGuid().ToString("N");
            item.OwnerFingerprint = request.Member?.Fingerprint ?? "";
            item.OwnerAlias = request.Member?.Alias ?? "未知成员";
            item.AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _state.Tray.Add(item);
            fileName = item.FileName;
            ownerAlias = item.OwnerAlias;
            snapshot = CloneState(_state);
        }

        Log.Info($"托盘新增: {fileName} 来自 {ownerAlias} (共 {snapshot!.Tray.Count} 项)");
        _ = BroadcastAsync(snapshot);
        return snapshot;
    }

    public RoomStateDto? HostRemoveTray(TrayRemoveRequestDto request)
    {
        RoomStateDto? snapshot;
        var removed = false;
        lock (_sync)
        {
            if (_role != RoomRole.Host || _code != request.Code || _state is null) return null;
            removed = _state.Tray.RemoveAll(t => t.Id == request.ItemId) > 0;
            snapshot = CloneState(_state);
        }

        if (removed)
        {
            Log.Info($"托盘移除: {Short(request.ItemId)} (共 {snapshot!.Tray.Count} 项)");
            _ = BroadcastAsync(snapshot);
        }
        return snapshot;
    }

    public void HostLeave(RoomLeaveRequestDto request)
    {
        RoomStateDto? snapshot;
        lock (_sync)
        {
            if (_role != RoomRole.Host || _code != request.Code || _state is null) return;
            _state.Members.RemoveAll(m => m.Fingerprint == request.Fingerprint);
            snapshot = CloneState(_state);
        }

        Log.Info($"成员离开房间 {request.Code}: {Short(request.Fingerprint)}");
        _ = BroadcastAsync(snapshot!);
    }

    private void HostHeartbeat()
    {
        var snapshot = Snapshot();
        if (snapshot is null) return;
        _ = BroadcastAsync(snapshot);
    }

    private async Task BroadcastAsync(RoomStateDto state)
    {
        List<(string Fingerprint, string Url)> targets;
        lock (_sync)
        {
            if (_state is null || _role != RoomRole.Host) return;
            targets = _state.Members
                .Where(m => m.Fingerprint != _settings.Fingerprint)
                .Select(m => (m.Fingerprint, $"http://{m.Ip}:{m.Port}/api/filetray/v1/room/update"))
                .ToList();
        }

        var dead = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                await Http.PostJsonAsync(target.Url, state, 3000).ConfigureAwait(false);
            }
            catch
            {
                dead.Add(target.Fingerprint);
            }
        }

        if (dead.Count == 0) return;

        // 连续两个心跳周期都推送失败的成员视为失联摘除;偶尔一次失败(网络抖动/进程瞬断)保留,等成员重连
        lock (_sync)
        {
            if (_state is null) return;
            foreach (var fingerprint in dead)
            {
                var misses = _missCounts.GetValueOrDefault(fingerprint, 0) + 1;
                _missCounts[fingerprint] = misses;
                if (misses >= 2)
                {
                    if (_state.Members.RemoveAll(m => m.Fingerprint == fingerprint) > 0)
                    {
                        Log.Warn($"成员连续 {misses} 次心跳无响应,已摘除: {Short(fingerprint)}");
                    }
                }
            }

            // 清理还在线成员的失败计数
            foreach (var fingerprint in _missCounts.Keys.ToList())
            {
                if (_state.Members.All(m => m.Fingerprint != fingerprint)) _missCounts.Remove(fingerprint);
            }
        }
    }

    // ============================ 成员侧 ============================

    public async Task JoinRoomAsync(string code)
    {
        if (IsInRoom) throw new InvalidOperationException("已在房间中,请先离开当前房间");

        var candidates = _discovery.GetDevices();
        if (candidates.Count == 0) throw new InvalidOperationException("尚未发现局域网内的任何设备,请稍候重试");

        // 并发探测所有已发现设备,谁在承载这个房间码谁就是房主
        var probes = candidates.Select(async device =>
        {
            try
            {
                var url = $"http://{device.Ip}:{device.Port}/api/filetray/v1/room/{Uri.EscapeDataString(code)}";
                var state = await Http.GetJsonAsync<RoomStateDto>(url, 1500).ConfigureAwait(false);
                return (Device: device, State: state);
            }
            catch
            {
                return (Device: device, State: (RoomStateDto?)null);
            }
        });
        var results = (await Task.WhenAll(probes).ConfigureAwait(false)).Where(r => r.State != null).ToList();
        if (results.Count == 0) throw new InvalidOperationException($"未找到房间 {code} 的房主,请确认房主已创建房间并在线");

        var host = results[0].Device;
        var hostUrl = $"http://{host.Ip}:{host.Port}";
        var joinRequest = new RoomJoinRequestDto
        {
            Code = code,
            Member = SelfMember(),
            SeenHostIp = host.Ip,
        };
        var state = await Http.PostJsonAsync<RoomStateDto>($"{hostUrl}/api/filetray/v1/room/join", joinRequest, 5000).ConfigureAwait(false)
            ?? throw new InvalidOperationException("加入房间失败: 房主无响应");

        lock (_sync)
        {
            _role = RoomRole.Member;
            _code = code;
            _hostBaseUrl = hostUrl;
            _state = state;
            _lastHostUpdateUtc = DateTime.UtcNow;
            _watchdogTimer ??= new Timer(_ => MemberWatchdog(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        Log.Info($"已加入房间 {code} @ {hostUrl} (成员 {state.Members.Count} 人, 托盘 {state.Tray.Count} 项)");
        RoomStateChanged?.Invoke();
    }

    /// <summary>接收房主推送的房间状态。</summary>
    public void ApplyUpdate(RoomStateDto state)
    {
        var closed = false;
        lock (_sync)
        {
            if (_role != RoomRole.Member || _code != state.Code) return;
            _lastHostUpdateUtc = DateTime.UtcNow;
            if (state.Closed)
            {
                closed = true;
                _role = RoomRole.None;
                _code = null;
                _state = null;
                _hostBaseUrl = null;
                _watchdogTimer?.Dispose();
                _watchdogTimer = null;
            }
            else
            {
                _state = state;
            }
        }

        if (closed)
        {
            Log.Info("房主关闭了房间");
            RoomClosed?.Invoke("房主关闭了房间");
        }
        else
        {
            RoomStateChanged?.Invoke();
        }
    }

    private void MemberWatchdog()
    {
        var dead = false;
        lock (_sync)
        {
            if (_role != RoomRole.Member) return;
            if ((DateTime.UtcNow - _lastHostUpdateUtc).TotalSeconds <= 15) return;
            dead = true;
            _role = RoomRole.None;
            _code = null;
            _state = null;
            _hostBaseUrl = null;
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
        }

        if (dead)
        {
            Log.Warn("超过 15 秒未收到房主心跳,已退出房间");
            RoomClosed?.Invoke("与房主失去连接");
        }
    }

    // ============================ 客户端操作(房主/成员通用) ============================

    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        foreach (var rawPath in paths)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            catch
            {
                Log.Warn($"路径无效,跳过: {rawPath}");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                Log.Warn($"文件不存在,跳过: {fullPath}");
                continue;
            }

            var info = new FileInfo(fullPath);
            var role = Role;
            if (role == RoomRole.None) throw new InvalidOperationException("尚未加入房间");

            if (role == RoomRole.Host)
            {
                RoomStateDto snapshot;
                lock (_sync)
                {
                    if (_role != RoomRole.Host || _state is null) return;
                    _state.Tray.Add(new TrayItemDto
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        OwnerFingerprint = _settings.Fingerprint,
                        OwnerAlias = _settings.Alias,
                        FileName = info.Name,
                        FilePath = fullPath,
                        FileSize = info.Length,
                        AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                    snapshot = CloneState(_state);
                }

                Log.Info($"托盘新增: {info.Name} 来自我 (共 {snapshot.Tray.Count} 项)");
                _ = BroadcastAsync(snapshot);
                RoomStateChanged?.Invoke();
            }
            else
            {
                string hostUrl;
                string code;
                lock (_sync)
                {
                    hostUrl = _hostBaseUrl ?? "";
                    code = _code ?? "";
                }

                if (hostUrl.Length == 0) throw new InvalidOperationException("尚未加入房间");
                var request = new TrayAddRequestDto
                {
                    Code = code,
                    Member = SelfMember(),
                    Item = new TrayItemDto { FileName = info.Name, FilePath = fullPath, FileSize = info.Length },
                };
                var state = await Http.PostJsonAsync<RoomStateDto>($"{hostUrl}/api/filetray/v1/room/tray/add", request, 8000).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("放入托盘失败: 房主无响应");
                lock (_sync)
                {
                    if (_role == RoomRole.Member && _code == state.Code)
                    {
                        _state = state;
                        _lastHostUpdateUtc = DateTime.UtcNow;
                    }
                }
                RoomStateChanged?.Invoke();
            }
        }
    }

    public async Task RemoveItemAsync(string itemId)
    {
        var role = Role;
        if (role == RoomRole.None) throw new InvalidOperationException("尚未加入房间");

        if (role == RoomRole.Host)
        {
            RoomStateDto? snapshot = null;
            lock (_sync)
            {
                if (_role == RoomRole.Host && _state != null)
                {
                    if (_state.Tray.RemoveAll(t => t.Id == itemId) > 0) snapshot = CloneState(_state);
                }
            }

            if (snapshot != null)
            {
                Log.Info($"托盘移除: {Short(itemId)} (共 {snapshot.Tray.Count} 项)");
                _ = BroadcastAsync(snapshot);
            }
            RoomStateChanged?.Invoke();
        }
        else
        {
            string hostUrl;
            string code;
            lock (_sync)
            {
                hostUrl = _hostBaseUrl ?? "";
                code = _code ?? "";
            }

            var state = await Http.PostJsonAsync<RoomStateDto>($"{hostUrl}/api/filetray/v1/room/tray/remove", new TrayRemoveRequestDto { Code = code, ItemId = itemId }, 8000).ConfigureAwait(false)
                ?? throw new InvalidOperationException("移除失败: 房主无响应");
            lock (_sync)
            {
                if (_role == RoomRole.Member && _code == state.Code)
                {
                    _state = state;
                    _lastHostUpdateUtc = DateTime.UtcNow;
                }
            }
            RoomStateChanged?.Invoke();
        }
    }

    public async Task LeaveRoomAsync()
    {
        var role = Role;
        var code = Code ?? "";
        if (role == RoomRole.Host)
        {
            var closing = Snapshot();
            if (closing != null)
            {
                closing.Closed = true;
                try
                {
                    await BroadcastAsync(closing).ConfigureAwait(false);
                }
                catch
                {
                    // 尽力而为
                }
            }
        }
        else if (role == RoomRole.Member)
        {
            string hostUrl;
            lock (_sync) hostUrl = _hostBaseUrl ?? "";
            if (hostUrl.Length > 0)
            {
                try
                {
                    await Http.PostJsonAsync($"{hostUrl}/api/filetray/v1/room/leave", new RoomLeaveRequestDto { Code = code, Fingerprint = _settings.Fingerprint }, 2000).ConfigureAwait(false);
                }
                catch
                {
                    // 房主可能已下线
                }
            }
        }

        ResetRoom();
        Log.Info($"已离开房间 {code}");
        RoomStateChanged?.Invoke();
    }

    /// <summary>从托盘条目的所有者机器上下载文件本体。</summary>
    public async Task DownloadItemAsync(string itemId, string savePath)
    {
        TrayItemDto item;
        MemberDto owner;
        string code;
        lock (_sync)
        {
            if (_state is null) throw new InvalidOperationException("尚未加入房间");
            item = _state.Tray.FirstOrDefault(t => t.Id == itemId) ?? throw new InvalidOperationException("托盘中没有该文件");
            owner = _state.Members.FirstOrDefault(m => m.Fingerprint == item.OwnerFingerprint) ?? throw new InvalidOperationException("找不到文件所有者");
            code = _state.Code;
        }

        if (owner.Fingerprint == _settings.Fingerprint)
            throw new InvalidOperationException("该文件就在本机: " + item.FilePath);

        var url = $"http://{owner.Ip}:{owner.Port}/api/filetray/v1/file?path={Uri.EscapeDataString(item.FilePath)}&code={Uri.EscapeDataString(code)}";
        await Http.DownloadToFileAsync(url, savePath).ConfigureAwait(false);
        Log.Info($"下载完成: {item.FileName} 来自 {owner.Alias} → {savePath}");
    }

    /// <summary>校验请求的路径确实在当前房间的托盘里(防止任意文件读取),返回实际路径。</summary>
    public string? ResolveTrayFile(string path, string code)
    {
        lock (_sync)
        {
            if (_state is null || !string.Equals(_code, code, StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var normalized = Path.GetFullPath(path);
                return _state.Tray.FirstOrDefault(t =>
                    string.Equals(Path.GetFullPath(t.FilePath), normalized, StringComparison.OrdinalIgnoreCase))?.FilePath;
            }
            catch
            {
                return null;
            }
        }
    }

    private void ResetRoom()
    {
        lock (_sync)
        {
            _role = RoomRole.None;
            _code = null;
            _state = null;
            _hostBaseUrl = null;
            _missCounts.Clear();
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
        }
    }

    /// <summary>进程退出时的尽力清理:房主广播关闭,成员通知房主。</summary>
    public void Shutdown()
    {
        try
        {
            var role = Role;
            var code = Code;
            if (role == RoomRole.Host)
            {
                var closing = Snapshot();
                if (closing != null)
                {
                    closing.Closed = true;
                    BroadcastAsync(closing).Wait(1500);
                }
            }
            else if (role == RoomRole.Member)
            {
                string hostUrl;
                lock (_sync) hostUrl = _hostBaseUrl ?? "";
                if (hostUrl.Length > 0)
                {
                    Http.PostJsonAsync($"{hostUrl}/api/filetray/v1/room/leave", new RoomLeaveRequestDto { Code = code ?? "", Fingerprint = _settings.Fingerprint }, 1500).Wait(1500);
                }
            }
        }
        catch
        {
            // 尽力而为
        }
        finally
        {
            ResetRoom();
        }
    }

    private static RoomStateDto CloneState(RoomStateDto state) => new()
    {
        Code = state.Code,
        Closed = state.Closed,
        HostFingerprint = state.HostFingerprint,
        Members = state.Members.Select(m => new MemberDto { Fingerprint = m.Fingerprint, Alias = m.Alias, Ip = m.Ip, Port = m.Port }).ToList(),
        Tray = state.Tray.Select(t => new TrayItemDto
        {
            Id = t.Id,
            OwnerFingerprint = t.OwnerFingerprint,
            OwnerAlias = t.OwnerAlias,
            FileName = t.FileName,
            FilePath = t.FilePath,
            FileSize = t.FileSize,
            AddedAt = t.AddedAt,
        }).ToList(),
    };

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];

    public void Dispose()
    {
        lock (_sync)
        {
            _heartbeatTimer?.Dispose();
            _watchdogTimer?.Dispose();
        }
    }
}
