using System;

namespace FileTray.Models;

/// <summary>已发现的局域网设备。</summary>
public sealed class DeviceRecord
{
    public required string Fingerprint { get; init; }
    public string Alias { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "http";
    public string? Room { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string Endpoint => $"{Ip}:{Port}";
}
