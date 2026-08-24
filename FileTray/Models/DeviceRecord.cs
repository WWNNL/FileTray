using System;
using System.Collections.Generic;

namespace FileTray.Models;

/// <summary>已发现的局域网节点(来自心跳广播或 HTTP register)。</summary>
public sealed class DeviceRecord
{
    public required string Fingerprint { get; init; }
    public string Alias { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "http";
    public HashSet<string> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastSeenUtc { get; set; }
    public string Endpoint => $"{Ip}:{Port}";
    public bool ContainsRoom(string code) => Rooms.Contains(code);
}
