using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileTray.Models;

namespace FileTray.Services;

/// <summary>
/// 延迟检测:每 5 秒对已发现的节点做一次 HTTP ping(GET /api/filetray/v1/ping),
/// 以往返耗时(RTT)作为该节点的延迟。心跳广播负责发现与 ID/房间更新,这里补上可量化的延迟。
/// </summary>
public sealed class LatencyService : IDisposable
{
    private readonly DiscoveryService _discovery;
    private readonly ConcurrentDictionary<string, int> _rttMs = new(); // 指纹 -> RTT 毫秒(-1 = 失败)
    private readonly ConcurrentDictionary<string, bool> _logged = new(); // 首次测量成功才记日志
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
        Log.Info("延迟检测已启动: 每 5 秒 ping 一次全部节点");
    }

    private async Task CycleAsync()
    {
        try
        {
            var devices = _discovery.GetDevices();
            foreach (var device in devices)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ok = true;
                try
                {
                    await Http.GetStringAsync($"http://{device.Ip}:{device.Port}/api/filetray/v1/ping", 2000).ConfigureAwait(false);
                }
                catch
                {
                    ok = false;
                }
                sw.Stop();

                var ms = ok ? (int)sw.ElapsedMilliseconds : -1;
                _rttMs[device.Fingerprint] = ms;
                if (ok && _logged.TryAdd(device.Fingerprint, true))
                    Log.Info($"延迟: {device.Alias} = {ms} ms");
            }

            // 清理已离线节点的记录
            var alive = devices.Select(d => d.Fingerprint).ToHashSet();
            foreach (var fingerprint in _rttMs.Keys)
            {
                if (!alive.Contains(fingerprint))
                {
                    _rttMs.TryRemove(fingerprint, out _);
                    _logged.TryRemove(fingerprint, out _);
                }
            }

            CycleCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"延迟检测异常: {ex.Message}");
        }
    }

    /// <summary>取节点最近一次的 RTT;从未成功测量过则返回 false。</summary>
    public bool TryGetLatency(string fingerprint, out int ms)
        => _rttMs.TryGetValue(fingerprint, out ms) && ms >= 0;

    public void Dispose() => _timer?.Dispose();
}
