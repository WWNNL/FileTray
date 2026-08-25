using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FileTray.Models;

namespace FileTray.Services;

/// <summary>
/// 分布式房间:没有房主,只有节点。
/// 每个节点在本地维护房间列表(持久化到 rooms.json),只有手动删除才移除,离线也留存。
/// 托盘状态(条目 + 删除墓碑)通过全量状态交换(gossip 反熵)在节点间收敛:
///   - 新增:按条目 Id 合并,冲突按 AddedAt 后写胜
///   - 删除:写入墓碑并同步,墓碑对同 Id 条目永久生效,防止离线节点迟到的旧数据复活
/// 节点发现依赖 DiscoveryService 的心跳广播(报文携带各自维护的房间码);
/// 同步轮次:对每个房间,向所有宣告该房间码的在线节点 POST 本地状态,并合并对方回传的状态。
/// </summary>
public sealed class RoomService : IDisposable
{
    private readonly object _sync = new();
    private readonly object _peerLogLock = new();
    private readonly SettingsService _settings;
    private readonly DiscoveryService _discovery;
    private readonly string _storePath;
    private readonly Dictionary<string, bool> _peerFailing = new(); // 指纹 -> 是否失联(仅用于日志节流)
    private Dictionary<string, RoomStore> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _syncTimer;
    private Timer? _saveDebounce;
    private int _syncRunning;

    public event Action? RoomsChanged;

    private sealed class RoomStore
    {
        public string Code = "";
        public long CreatedAt;
        public Dictionary<string, TrayItemDto> Items = new();
        public Dictionary<string, TombstoneDto> Tombstones = new();
    }

    private sealed class PersistedRoom
    {
        public string Code { get; set; } = "";
        public long CreatedAt { get; set; }
        public List<TrayItemDto> Items { get; set; } = new();
        public List<TombstoneDto> Tombstones { get; set; } = new();
    }

    private sealed class PersistedRoot
    {
        public List<PersistedRoom> Rooms { get; set; } = new();
    }

    public RoomService(SettingsService settings, DiscoveryService discovery)
    {
        _settings = settings;
        _discovery = discovery;
        _storePath = Path.Combine(settings.DirectoryPath, "rooms.json");
        Load();
        _saveDebounce = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>启动周期同步(每 3 秒一轮,与所有可连通的房间节点交换状态)。</summary>
    public void Start()
    {
        _syncTimer = new Timer(_ => _ = SyncRoundAsync(), null, TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(3));
        Log.Info($"房间服务已启动: 本地维护 {_rooms.Count} 个房间");
    }

    public IReadOnlyList<string> RoomCodes
    {
        get { lock (_sync) return _rooms.Keys.ToList(); }
    }

    public IReadOnlyList<RoomSummary> GetRoomSummaries()
    {
        lock (_sync)
        {
            return _rooms.Values
                .Select(r => new RoomSummary(r.Code, r.Items.Values.Count(i => !r.Tombstones.ContainsKey(i.Id))))
                .OrderBy(r => r.Code, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>生成 8 位随机房间码(大写字母 + 数字),避免与已有房间重复。</summary>
    private string GenerateRoomCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
            var code = new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
            lock (_sync)
            {
                if (!_rooms.ContainsKey(code)) return code;
            }
        }
        return Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }

    private static bool IsValidRoomCode(string code)
        => code.Length == 8 && code.All(c => c is ((>= 'A' and <= 'Z') or (>= '0' and <= '9')));

    /// <summary>
    /// 在本地开始维护一个房间:创建(生成新码)与加入(指定码)对本节点是同一件事,
    /// 都只是把房间加入本地列表;有其他节点宣告同一房间码时,心跳发现后自动开始同步。
    /// </summary>
    public string CreateRoom(string? fixedCode = null)
    {
        string code;
        if (fixedCode is null)
        {
            code = GenerateRoomCode();
        }
        else
        {
            code = fixedCode.Trim().ToUpperInvariant();
            if (!IsValidRoomCode(code)) throw new ArgumentException("房间码须为 8 位大写字母/数字");
        }

        lock (_sync)
        {
            if (_rooms.ContainsKey(code)) throw new InvalidOperationException("该房间已在本地列表中");
            _rooms[code] = new RoomStore { Code = code, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        }

        SaveNow();
        Log.Info($"开始维护房间: {code}");
        RoomsChanged?.Invoke();
        SyncNow();
        return code;
    }

    /// <summary>手动删除房间(仅影响本机;其他节点仍各自维护,直到它们手动删除)。</summary>
    public void DeleteRoom(string code)
    {
        lock (_sync)
        {
            if (!_rooms.Remove(code)) return;
        }

        SaveNow();
        Log.Info($"已从本机删除房间: {code}(其他节点不受影响)");
        RoomsChanged?.Invoke();
    }

    /// <summary>
    /// 把本地文件放入房间托盘(条目所有者为本机),保存并立即向节点推送。
    /// </summary>
    public void AddFiles(string code, IEnumerable<string> paths)
    {
        var added = new List<TrayItemDto>();
        foreach (var rawPath in paths)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(rawPath); }
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
            added.Add(new TrayItemDto
            {
                Id = Guid.NewGuid().ToString("N"),
                OwnerFingerprint = _settings.Fingerprint,
                OwnerAlias = _settings.Alias,
                FileName = info.Name,
                FilePath = fullPath,
                FileSize = info.Length,
                AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        if (added.Count == 0) return;

        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) throw new InvalidOperationException("房间不存在(可能已被删除)");
            foreach (var item in added)
            {
                if (room.Tombstones.ContainsKey(item.Id)) continue; // 理论上新 Id 不会命中墓碑,防御性判断
                room.Items[item.Id] = item;
            }
        }

        foreach (var item in added) Log.Info($"托盘新增: 房间 {code} {item.FileName}(共 {GetVisibleItems(code).Count} 项)");
        SaveSoon();
        RoomsChanged?.Invoke();
        SyncNow();
    }

    /// <summary>
    /// 从托盘移除条目:写入墓碑并同步到所有节点。条目保留在 Items 表中,
    /// 由墓碑决定可见性,防止远端用旧状态复活已删除条目。
    /// </summary>
    public void RemoveItem(string code, string itemId)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) throw new InvalidOperationException("房间不存在(可能已被删除)");
            var tomb = new TombstoneDto
            {
                ItemId = itemId,
                DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeletedBy = _settings.Fingerprint,
            };
            if (!room.Tombstones.TryGetValue(itemId, out var cur) || cur.DeletedAt < tomb.DeletedAt)
                room.Tombstones[itemId] = tomb;
        }

        Log.Info($"托盘移除: 房间 {code} {Short(itemId)}");
        SaveSoon();
        RoomsChanged?.Invoke();
        SyncNow();
    }

    /// <summary>房间当前可见条目(排除已墓碑的),新条目在前。</summary>
    public IReadOnlyList<TrayItemDto> GetVisibleItems(string code)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return Array.Empty<TrayItemDto>();
            return room.Items.Values
                .Where(i => !room.Tombstones.ContainsKey(i.Id))
                .OrderByDescending(i => i.AddedAt)
                .Select(CloneItem)
                .ToList();
        }
    }

    private static bool IsTombstoned(RoomStore room, string itemId)
        => room.Tombstones.ContainsKey(itemId);

    /// <summary>本地完整状态(条目 + 墓碑),用于调试接口与同步报文。</summary>
    public RoomSyncDto? GetLocalState(string code)
    {
        lock (_sync)
        {
            return _rooms.TryGetValue(code, out var room) ? BuildSyncDto(room) : null;
        }
    }

    /// <summary>
    /// 合并远端节点发来的状态,返回合并后的本地状态(供对方合并)。
    /// 本节点不维护该房间时返回 null(调用方应回 404)。
    /// </summary>
    public RoomSyncDto? MergeSync(RoomSyncDto remote)
    {
        RoomSyncDto response;
        bool changed;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(remote.Code, out var room)) return null;
            changed = MergeInto(room, remote);
            response = BuildSyncDto(room);
        }

        if (changed)
        {
            SaveSoon();
            RoomsChanged?.Invoke();
        }
        return response;
    }

    private static bool MergeInto(RoomStore room, RoomSyncDto remote)
    {
        var changed = false;

        foreach (var tomb in remote.Tombstones)
        {
            if (tomb.ItemId.Length == 0) continue;
            if (room.Tombstones.TryGetValue(tomb.ItemId, out var cur) && cur.DeletedAt >= tomb.DeletedAt) continue;
            room.Tombstones[tomb.ItemId] = tomb;
            changed = true;
            if (room.Items.ContainsKey(tomb.ItemId))
                Log.Info($"同步: 房间 {room.Code} 移除条目 {Short(tomb.ItemId)}(由 {tomb.DeletedBy[..Math.Min(8, tomb.DeletedBy.Length)]} 删除)");
        }

        // 先合墓碑再合条目:已被墓碑的条目拒收,防止离线节点迟到的旧数据复活已删除内容
        foreach (var item in remote.Items)
        {
            if (item.Id.Length == 0 || room.Tombstones.ContainsKey(item.Id)) continue;
            if (room.Items.TryGetValue(item.Id, out var cur) && cur.AddedAt >= item.AddedAt) continue;
            room.Items[item.Id] = item;
            changed = true;
            Log.Info($"同步: 房间 {room.Code} 新增 {item.FileName} 来自 {item.OwnerAlias}");
        }

        return changed;
    }

    /// <summary>
    /// 从条目所有者的机器下载文件本体;progress 周期性上报下载进度。
    /// </summary>
    public async Task DownloadItemAsync(string code, string itemId, string savePath,
        IProgress<(int Percent, long Received, long Total)>? progress = null)
    {
        TrayItemDto? item;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)
                || !room.Items.TryGetValue(itemId, out item)
                || room.Tombstones.ContainsKey(itemId))
                throw new InvalidOperationException("托盘中没有该文件(可能已被删除)");
        }

        if (item.OwnerFingerprint == _settings.Fingerprint)
            throw new InvalidOperationException("该文件就在本机: " + item.FilePath);

        var owner = _discovery.GetDevices().FirstOrDefault(d => d.Fingerprint == item.OwnerFingerprint)
            ?? throw new InvalidOperationException($"文件所有者 {item.OwnerAlias} 当前不在线(未发现该设备)");

        var url = $"http://{owner.Ip}:{owner.Port}/api/filetray/v1/file?path={Uri.EscapeDataString(item.FilePath)}&code={Uri.EscapeDataString(code)}";
        await Http.DownloadToFileAsync(url, savePath, progress).ConfigureAwait(false);
        Log.Info($"下载完成: {item.FileName} 来自 {owner.Alias} → {savePath}");
    }

    /// <summary>
    /// 校验下载请求的路径确实是本机放入该房间托盘的文件(防任意文件读取),返回实际路径。
    /// </summary>
    public string? ValidateOwnFile(string path, string code)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return null;
            try
            {
                var normalized = Path.GetFullPath(path);
                return room.Items.Values
                    .Where(i => i.OwnerFingerprint == _settings.Fingerprint && !room.Tombstones.ContainsKey(i.Id))
                    .FirstOrDefault(i => string.Equals(Path.GetFullPath(i.FilePath), normalized, StringComparison.OrdinalIgnoreCase))
                    ?.FilePath;
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task SyncRoundAsync()
    {
        if (Interlocked.CompareExchange(ref _syncRunning, 1, 0) != 0) return;
        try
        {
            var devices = _discovery.GetDevices();
            List<string> codes;
            lock (_sync) codes = _rooms.Keys.ToList();

            foreach (var code in codes)
            {
                var peers = devices.Where(d => d.ContainsRoom(code)).ToList();
                if (peers.Count == 0) continue;

                RoomSyncDto myState;
                lock (_sync)
                {
                    if (!_rooms.TryGetValue(code, out var room)) continue;
                    myState = BuildSyncDto(room);
                }

                var requests = peers.Select(async peer =>
                {
                    try
                    {
                        var state = await Http.PostJsonAsync<RoomSyncDto>(
                            $"http://{peer.Ip}:{peer.Port}/api/filetray/v1/room/sync",
                            myState, 3500).ConfigureAwait(false);
                        MarkPeer(peer.Fingerprint, ok: true);
                        return state;
                    }
                    catch (Exception ex)
                    {
                        MarkPeer(peer.Fingerprint, ok: false, ex.Message);
                        return null;
                    }
                }).ToList();

                var responses = await Task.WhenAll(requests).ConfigureAwait(false);
                foreach (var response in responses)
                {
                    if (response != null) MergeSync(response);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"同步轮次异常: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _syncRunning, 0);
        }
    }

    /// <summary>本地状态变化后立即触发一轮同步(有重入保护,不阻塞调用方)。</summary>
    public void SyncNow() => _ = Task.Run(SyncRoundAsync);

    private void MarkPeer(string fingerprint, bool ok, string? error = null)
    {
        lock (_peerLogLock)
        {
            var was = _peerFailing.GetValueOrDefault(fingerprint, false);
            if (!ok && !was)
            {
                _peerFailing[fingerprint] = true;
                Log.Warn($"节点 {Short(fingerprint)} 同步失败: {error}");
            }
            else if (ok && was)
            {
                _peerFailing[fingerprint] = false;
                Log.Info($"节点 {Short(fingerprint)} 同步恢复");
            }
        }
    }

    // ============================ 持久化 ============================

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var root = JsonSerializer.Deserialize<PersistedRoot>(File.ReadAllText(_storePath), Http.Json);
            if (root is null) return;
            foreach (var room in root.Rooms)
            {
                if (string.IsNullOrWhiteSpace(room.Code)) continue;
                var store = new RoomStore { Code = room.Code, CreatedAt = room.CreatedAt };
                foreach (var item in room.Items)
                    if (item.Id.Length > 0) store.Items[item.Id] = item;
                foreach (var tomb in room.Tombstones)
                    if (tomb.ItemId.Length > 0) store.Tombstones[tomb.ItemId] = tomb;
                _rooms[room.Code] = store;
            }
            Log.Info($"已加载本地房间 {root.Rooms.Count} 个");
        }
        catch (Exception ex)
        {
            Log.Warn($"读取房间数据失败: {ex.Message}");
        }
    }

    private void SaveNow()
    {
        try
        {
            PersistedRoot root;
            lock (_sync)
            {
                root = new PersistedRoot
                {
                    Rooms = _rooms.Values.Select(r => new PersistedRoom
                    {
                        Code = r.Code,
                        CreatedAt = r.CreatedAt,
                        Items = r.Items.Values.Select(CloneItem).ToList(),
                        Tombstones = r.Tombstones.Values.Select(t => new TombstoneDto
                        {
                            ItemId = t.ItemId, DeletedAt = t.DeletedAt, DeletedBy = t.DeletedBy,
                        }).ToList(),
                    }).ToList(),
                };
            }
            File.WriteAllText(_storePath, JsonSerializer.Serialize(root, Http.Json));
        }
        catch (Exception ex)
        {
            Log.Warn($"保存房间数据失败: {ex.Message}");
        }
    }

    /// <summary>防抖保存:2 秒内的连续变更合并为一次写盘。</summary>
    private void SaveSoon() => _saveDebounce?.Change(2000, Timeout.Infinite);

    private static RoomSyncDto BuildSyncDto(RoomStore room) => new()
    {
        Code = room.Code,
        Items = room.Items.Values.Select(CloneItem).ToList(),
        Tombstones = room.Tombstones.Values.Select(t => new TombstoneDto
        {
            ItemId = t.ItemId, DeletedAt = t.DeletedAt, DeletedBy = t.DeletedBy,
        }).ToList(),
    };

    private static TrayItemDto CloneItem(TrayItemDto t) => new()
    {
        Id = t.Id,
        OwnerFingerprint = t.OwnerFingerprint,
        OwnerAlias = t.OwnerAlias,
        FileName = t.FileName,
        FilePath = t.FilePath,
        FileSize = t.FileSize,
        AddedAt = t.AddedAt,
    };

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];

    /// <summary>进程退出:立即落盘。</summary>
    public void Shutdown()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
        SaveNow();
    }

    public void Dispose() => Shutdown();
}
