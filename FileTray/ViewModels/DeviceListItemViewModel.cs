using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FileTray.Models;

namespace FileTray.ViewModels;

/// <summary>设备的一个地址条目:显示 IP + 延迟,点击复制 IP。</summary>
public partial class DeviceEndpointViewModel : ViewModelBase
{
    public string Ip { get; }

    [ObservableProperty] private string _displayText = "";

    public Action<DeviceEndpointViewModel>? CopyRequested { get; set; }

    public DeviceEndpointViewModel(string ip)
    {
        Ip = ip;
    }
}

public partial class DeviceListItemViewModel : ViewModelBase
{
    private readonly Dictionary<string, DeviceEndpointViewModel> _endpointVms = new(StringComparer.Ordinal);

    public DeviceRecord Record { get; private set; }

    public string Fingerprint => Record.Fingerprint;

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _latencyText = "—";

    /// <summary>全部地址(点击复制 IP),按延迟从低到高排序。</summary>
    public ObservableCollection<DeviceEndpointViewModel> Endpoints { get; } = new();

    public Action<string>? CopyIpRequested { get; set; }

    public DeviceListItemViewModel(DeviceRecord record)
    {
        Record = record;
        Update(record);
    }

    public void Update(DeviceRecord record)
    {
        Record = record;
        Alias = record.Alias;

        // 地址条目按 IP 复用实例,只刷新文本
        var sorted = record.SnapshotEndpoints()
            .OrderBy(e => e.RttMs >= 0 ? e.RttMs : int.MaxValue)
            .ThenBy(e => e.Ip, StringComparer.Ordinal)
            .ToList();

        for (var i = Endpoints.Count - 1; i >= 0; i--)
        {
            if (sorted.All(e => e.Ip != Endpoints[i].Ip)) Endpoints.RemoveAt(i);
        }

        foreach (var endpoint in sorted)
        {
            if (!_endpointVms.TryGetValue(endpoint.Ip, out var vm))
            {
                vm = new DeviceEndpointViewModel(endpoint.Ip)
                {
                    CopyRequested = e => CopyIpRequested?.Invoke(e.Ip),
                };
                _endpointVms[endpoint.Ip] = vm;
            }
            vm.DisplayText = $"{endpoint.Ip} ({(endpoint.RttMs >= 0 ? endpoint.RttMs + "ms" : "…")})";

            var index = sorted.FindIndex(e => e.Ip == endpoint.Ip);
            var existingIndex = Endpoints.IndexOf(vm);
            if (existingIndex < 0) Endpoints.Insert(Math.Min(index, Endpoints.Count), vm);
            else if (existingIndex != index)
            {
                Endpoints.RemoveAt(existingIndex);
                Endpoints.Insert(Math.Min(index, Endpoints.Count), vm);
            }
        }
    }
}
