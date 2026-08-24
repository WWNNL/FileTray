using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FileTray.Models;

namespace FileTray.Services;

/// <summary>
/// 基于 UDP 多播的设备发现,照搬 LocalSend v2 的默认方式:
/// 周期性向 224.0.0.167:53317 广播自身信息(announce=true),
/// 收到他人的广播后立即回应一次自身信息(announce=false,限流 1 秒)以加快互相发现。
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public const int MulticastPort = 53317;
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.167");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastGroup, MulticastPort);

    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new();
    private readonly UdpClient _receiver;
    private readonly Socket _sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _cts = new();
    private List<IPAddress> _interfaceAddresses = new();
    private DateTime _interfacesRefreshedUtc = DateTime.MinValue;
    private DateTime _lastReplyUtc = DateTime.MinValue;
    private Timer? _announceTimer;
    private Timer? _pruneTimer;

    private Func<string> _aliasProvider = () => Environment.MachineName;
    private Func<string> _fingerprintProvider = () => "";
    private Func<int> _portProvider = () => 0;
    private Func<string?> _roomProvider = () => null;

    public event Action? DevicesChanged;

    public DiscoveryService()
    {
        // ReuseAddress 允许多个 FileTray(或 LocalSend)实例同时监听 53317 端口接收多播
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        _receiver = new UdpClient { Client = socket };
    }

    public void Start(
        Func<string> aliasProvider,
        Func<string> fingerprintProvider,
        Func<int> portProvider,
        Func<string?> roomProvider)
    {
        _aliasProvider = aliasProvider;
        _fingerprintProvider = fingerprintProvider;
        _portProvider = portProvider;
        _roomProvider = roomProvider;

        _sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        _sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        RefreshInterfaces();

        _ = Task.Run(ReceiveLoopAsync);
        _announceTimer = new Timer(_ => TryAnnounce(announce: true), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        _pruneTimer = new Timer(_ => Prune(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        Log.Info($"发现服务已启动: 多播 {MulticastGroup}:{MulticastPort}");
    }

    public IReadOnlyList<DeviceRecord> GetDevices() =>
        _devices.Values.OrderBy(d => d.Alias, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Endpoint).ToList();

    /// <summary>登记一台设备(来自多播公告或 HTTP register)。</summary>
    public void Record(DeviceInfoDto info, IPAddress remote)
    {
        if (string.IsNullOrEmpty(info.Fingerprint) || info.Fingerprint == _fingerprintProvider()) return;

        var remoteIp = NetUtil.NormalizeIp(remote.ToString());
        var isNew = false;
        _devices.AddOrUpdate(
            info.Fingerprint,
            _ =>
            {
                isNew = true;
                return new DeviceRecord
                {
                    Fingerprint = info.Fingerprint,
                    Alias = info.Alias,
                    Ip = remoteIp,
                    Port = info.Port > 0 ? info.Port : 53317,
                    Protocol = string.IsNullOrEmpty(info.Protocol) ? "http" : info.Protocol,
                    Room = string.IsNullOrEmpty(info.Room) ? null : info.Room,
                    LastSeenUtc = DateTime.UtcNow,
                };
            },
            (_, existing) =>
            {
                existing.Alias = info.Alias;
                existing.Ip = remoteIp;
                if (info.Port > 0) existing.Port = info.Port;
                if (!string.IsNullOrEmpty(info.Protocol)) existing.Protocol = info.Protocol;
                existing.Room = string.IsNullOrEmpty(info.Room) ? null : info.Room;
                existing.LastSeenUtc = DateTime.UtcNow;
                return existing;
            });

        if (isNew) Log.Info($"发现设备: {info.Alias} ({remoteIp}:{(info.Port > 0 ? info.Port : 53317)})");
        DevicesChanged?.Invoke();
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _receiver.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                HandlePacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"接收多播数据失败: {ex.Message}");
            }
        }
    }

    private void HandlePacket(byte[] buffer, IPEndPoint remote)
    {
        try
        {
            var info = JsonSerializer.Deserialize<DeviceInfoDto>(buffer, Http.Json);
            if (info is null || string.IsNullOrEmpty(info.Fingerprint)) return;
            Record(info, remote.Address);

            // 收到他人广播时立即回应一次;回应里 announce=false,接收方不会再回应,不会形成风暴
            if (info.Announce
                && (DateTime.UtcNow - _lastReplyUtc).TotalMilliseconds > 1000)
            {
                _lastReplyUtc = DateTime.UtcNow;
                TryAnnounce(announce: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析发现消息失败({remote}): {ex.Message}");
        }
    }

    private void TryAnnounce(bool announce)
    {
        try
        {
            var port = _portProvider();
            if (port <= 0) return;

            var dto = new DeviceInfoDto
            {
                Alias = _aliasProvider(),
                Version = "2.0",
                DeviceModel = Environment.OSVersion.VersionString,
                DeviceType = "desktop",
                Fingerprint = _fingerprintProvider(),
                Port = port,
                Protocol = "http",
                Download = false,
                Announce = announce,
                App = "filetray",
                Room = _roomProvider(),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, Http.Json);

            // 每分钟刷新一次网卡列表,新出现的网卡也会加入多播组
            if ((DateTime.UtcNow - _interfacesRefreshedUtc).TotalSeconds > 60) RefreshInterfaces();

            var sent = false;
            foreach (var address in _interfaceAddresses)
            {
                try
                {
                    _sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    _sender.SendTo(bytes, MulticastEndpoint);
                    sent = true;
                }
                catch (Exception ex)
                {
                    Log.Warn($"多播发送失败({address}): {ex.Message}");
                }
            }

            if (!sent)
            {
                try { _sender.SendTo(bytes, MulticastEndpoint); } catch { /* 忽略 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"发送公告失败: {ex.Message}");
        }
    }

    private void RefreshInterfaces()
    {
        _interfacesRefreshedUtc = DateTime.UtcNow;
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                            && n.SupportsMulticast)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .Distinct()
                .ToList();
            if (addresses.Count > 0) _interfaceAddresses = addresses;

            foreach (var address in _interfaceAddresses)
            {
                try
                {
                    _receiver.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup, address));
                }
                catch (Exception ex)
                {
                    Log.Warn($"加入多播组失败({address}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举网卡失败: {ex.Message}");
        }
    }

    private void Prune()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-12);
            var expired = _devices.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList();
            if (expired.Count == 0) return;
            foreach (var fingerprint in expired)
            {
                if (_devices.TryRemove(fingerprint, out var device))
                {
                    Log.Info($"设备离线: {device.Alias} ({device.Endpoint})");
                }
            }
            DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"清理离线设备失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _announceTimer?.Dispose();
        _pruneTimer?.Dispose();
        try { _receiver.Close(); } catch { }
        try { _sender.Close(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
