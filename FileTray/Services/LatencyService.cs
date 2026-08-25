using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileTray.Models;

namespace FileTray.Services;

/// <summary>
/// 延迟检测:每 5 秒对已发现节点的**每个地址**做一次 HTTP ping(GET /api/filetray/v1/ping),
/// 往返耗时(RTT)记录在该地址上;设备级延迟取所有地址的最小值,
/// 连接(发文本/同步/下载)使用的地址即延迟最低的那个。
/// </summary>
public sealed class LatencyService : IDisposable
{
    private readonly DiscoveryService _discovery;
    private readonly ConcurrentDictionary<string, int> _bestRttMs = new(); // 指纹 -> 最优 RTT(-1 = 全部失败)
    private readonly ConcurrentDictionary<string, bool> _logged = new(); // "指纹|ip" -> 首次测量成功才记日志
    private Timer? _timer;

    /// <summary>每轮测量完成后触发(UI 刷新延迟显示)。</summary>
    public event Action? CycleCompleted;

    public LatencyService(DiscoveryService discovery)
    {
        _discovery = discovery;
    }

    public void Start()
    {
        _timer = new Timer(_ => _ = CycleAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        Log.Info("延迟检测已启动: 每 5 秒逐地址 ping 全部节点");
    }

    private async Task CycleAsync()
    {
        try
        {
            var devices = _discovery.GetDevices();
            var aliveFingerprints = new HashSet<string>();

            foreach (var device in devices)
            {
                aliveFingerprints.Add(device.Fingerprint);
                var endpoints = device.SnapshotEndpoints();

                foreach (var endpoint in endpoints)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ok = true;
                    try
                    {
                        await Http.GetStringAsync($"http://{endpoint.Ip}:{endpoint.Port}/api/filetray/v1/ping", 2000).ConfigureAwait(false);
                    }
                    catch
                    {
                        ok = false;
                    }
                    sw.Stop();

                    var ms = ok ? (int)sw.ElapsedMilliseconds : -1;
                    device.UpdateRtt(endpoint.Ip, ms);
                    if (ok && _logged.TryAdd($"{device.Fingerprint}|{endpoint.Ip}", true))
                    {
                        Log.Info($"延迟: {device.Alias} ({endpoint.Ip}) = {ms} ms");
                    }
                }

                // 设备级延迟 = 各地址最小 RTT
                var measured = device.SnapshotEndpoints().Where(e => e.RttMs >= 0).ToList();
                _bestRttMs[device.Fingerprint] = measured.Count > 0 ? measured.Min(e => e.RttMs) : -1;
            }

            // 清理已离线节点的记录
            foreach (var fingerprint in _bestRttMs.Keys)
            {
                if (!aliveFingerprints.Contains(fingerprint))
                {
                    _bestRttMs.TryRemove(fingerprint, out _);
                }
            }
            foreach (var key in _logged.Keys)
            {
                var fingerprint = key.Split('|')[0];
                if (!aliveFingerprints.Contains(fingerprint))
                {
                    _logged.TryRemove(key, out _);
                }
            }

            CycleCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"延迟检测异常: {ex.Message}");
        }
    }

    /// <summary>取节点所有地址中的最优 RTT;从未成功测量过则返回 false。</summary>
    public bool TryGetLatency(string fingerprint, out int ms)
        => _bestRttMs.TryGetValue(fingerprint, out ms) && ms >= 0;

    public void Dispose() => _timer?.Dispose();
}
