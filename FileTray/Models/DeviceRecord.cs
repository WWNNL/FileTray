using System;
using System.Collections.Generic;
using System.Linq;

namespace FileTray.Models;

/// <summary>设备的单个可达地址(一块网卡/一条路由对应一个 IP)。</summary>
public sealed class DeviceEndpoint
{
    public required string Ip { get; init; }
    public int Port { get; set; }
    public DateTime LastSeenUtc { get; set; }
    /// <summary>最近一次 ping 的往返毫秒数;-1 = 未测或失败。</summary>
    public int RttMs { get; set; } = -1;
}

/// <summary>
/// 已发现的局域网节点。同一指纹(同一台设备)可能经由多个 IP 可达
/// (多网卡/VPN/虚拟网卡),合并为一个记录;连接时取延迟最低的地址。
/// </summary>
public sealed class DeviceRecord
{
    private readonly object _gate = new();
    private readonly List<DeviceEndpoint> _endpoints = new();

    public required string Fingerprint { get; init; }
    public string Alias { get; set; } = "";
    public string Protocol { get; set; } = "http";
    public HashSet<string> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastSeenUtc { get; set; }

    /// <summary>延迟最低的地址;全部未测出延迟时回退到最近见过的地址。</summary>
    public DeviceEndpoint? BestEndpoint()
    {
        lock (_gate)
        {
            if (_endpoints.Count == 0) return null;
            var measured = _endpoints
                .Where(e => e.RttMs >= 0)
                .OrderBy(e => e.RttMs)
                .ThenBy(e => e.Ip, StringComparer.Ordinal)
                .ToList();
            return measured.Count > 0
                ? measured[0]
                : _endpoints.OrderByDescending(e => e.LastSeenUtc).First();
        }
    }

    public IReadOnlyList<DeviceEndpoint> SnapshotEndpoints()
    {
        lock (_gate) return _endpoints.ToList();
    }

    public int EndpointCount
    {
        get { lock (_gate) return _endpoints.Count; }
    }

    public string Ip => BestEndpoint()?.Ip ?? "";
    public int Port => BestEndpoint()?.Port ?? 0;
    public string Endpoint => $"{Ip}:{Port}";

    public bool ContainsRoom(string code) => Rooms.Contains(code);

    internal DeviceEndpoint UpsertEndpoint(string ip, int port, DateTime seenUtc)
    {
        lock (_gate)
        {
            var ep = _endpoints.FirstOrDefault(e => e.Ip == ip);
            if (ep is null)
            {
                ep = new DeviceEndpoint { Ip = ip, Port = port, LastSeenUtc = seenUtc };
                _endpoints.Add(ep);
            }
            else
            {
                ep.Port = port;
                ep.LastSeenUtc = seenUtc;
            }
            return ep;
        }
    }

    /// <summary>摘除超时未见的地址(某块网卡下线),返回是否发生了变化。</summary>
    internal bool PruneEndpoints(DateTime cutoff)
    {
        lock (_gate)
        {
            var before = _endpoints.Count;
            _endpoints.RemoveAll(e => e.LastSeenUtc < cutoff);
            return _endpoints.Count != before;
        }
    }

    internal void UpdateRtt(string ip, int rttMs)
    {
        lock (_gate)
        {
            var ep = _endpoints.FirstOrDefault(e => e.Ip == ip);
            if (ep != null) ep.RttMs = rttMs;
        }
    }
}
