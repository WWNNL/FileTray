using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTray.Models;
using FileTray.Services;

namespace FileTray.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly DiscoveryService _discovery;
    private readonly HttpApiService _server;
    private readonly RoomService _room;
    private readonly TransferService _transfer;
    private readonly LatencyService _latency;

    private int _refreshQueued;
    private bool _updatingDetail;

    /// <summary>由 MainWindow 在打开后注入的文件选择器(打开多个文件)。</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFilesAsync { get; set; }

    /// <summary>由 MainWindow 在打开后注入的保存路径选择器。</summary>
    public Func<string, Task<string?>>? PickSaveFileAsync { get; set; }

    [ObservableProperty] private string _ownInfoText = "启动中…";
    [ObservableProperty] private string _aliasInput = "";
    [ObservableProperty] private string _devicesHeader = "附近设备 (0)";
    [ObservableProperty] private DeviceListItemViewModel? _selectedDevice;
    [ObservableProperty] private string _messageInput = "";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _joinCodeInput = "";
    [ObservableProperty] private bool _hasRooms;
    [ObservableProperty] private bool _isRoomSelected;
    [ObservableProperty] private string _roomCode = "";
    [ObservableProperty] private string _trayHeader = "";

    [ObservableProperty] private RoomListItemViewModel? _selectedRoom;
    [ObservableProperty] private MemberListItemViewModel? _selectedMember;

    public ObservableCollection<DeviceListItemViewModel> Devices { get; } = new();
    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();
    public ObservableCollection<RoomListItemViewModel> Rooms { get; } = new();
    public ObservableCollection<MemberListItemViewModel> Members { get; } = new();
    public ObservableCollection<TrayItemViewModel> TrayItems { get; } = new();

    public MainWindowViewModel(
        SettingsService settings,
        DiscoveryService discovery,
        HttpApiService server,
        RoomService room,
        TransferService transfer,
        LatencyService latency)
    {
        _settings = settings;
        _discovery = discovery;
        _server = server;
        _room = room;
        _transfer = transfer;
        _latency = latency;
        _aliasInput = settings.Alias;

        _discovery.DevicesChanged += ScheduleRefresh;
        _latency.CycleCompleted += ScheduleRefresh;
        _room.RoomsChanged += ScheduleRefresh;
        _server.TextReceived += (alias, _, text) => Dispatcher.UIThread.Post(() => OnTextReceived(alias, text));
        _server.FileReceived += (alias, fileName, _, savedPath) => Dispatcher.UIThread.Post(() => OnFileReceived(alias, fileName, savedPath));
    }

    public void NotifyStarted(int port)
    {
        OwnInfoText = $"本机: {_settings.Alias} · 指纹 {_settings.Fingerprint[..8]}… · 端口 {port}";
        AliasInput = _settings.Alias;
    }

    public void NotifyStartupFailed(string message)
    {
        OwnInfoText = "启动失败(详见日志)";
        StatusText = $"启动失败: {message}";
    }

    // ============================ 刷新(事件合流,避免高频重建) ============================

    private void ScheduleRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { RefreshAll(); }
            finally { Volatile.Write(ref _refreshQueued, 0); }
        });
    }

    private void RefreshAll()
    {
        RefreshDevices();
        RefreshRooms();
    }

    private string FormatLatency(string fingerprint)
        => _latency.TryGetLatency(fingerprint, out var ms) ? $"{ms} ms" : "—";

    private void RefreshDevices()
    {
        var records = _discovery.GetDevices();

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (records.All(r => r.Fingerprint != Devices[i].Fingerprint))
            {
                Devices.RemoveAt(i);
            }
        }

        foreach (var record in records)
        {
            var existing = Devices.FirstOrDefault(d => d.Fingerprint == record.Fingerprint);
            if (existing is null)
            {
                var vm = new DeviceListItemViewModel(record) { LatencyText = FormatLatency(record.Fingerprint) };
                Devices.Add(vm);
            }
            else
            {
                existing.Update(record);
                existing.LatencyText = FormatLatency(record.Fingerprint);
            }
        }

        DevicesHeader = $"附近设备 ({Devices.Count})";
    }

    private void RefreshRooms()
    {
        var summaries = _room.GetRoomSummaries();
        var devices = _discovery.GetDevices();
        var selectedCode = SelectedRoom?.Code;

        Rooms.Clear();
        foreach (var summary in summaries)
        {
            var nodes = devices.Count(d => d.ContainsRoom(summary.Code)) + 1; // + 本机
            Rooms.Add(new RoomListItemViewModel(summary.Code, $"{summary.ItemCount} 个文件 · {nodes} 个节点"));
        }
        HasRooms = Rooms.Count > 0;

        _updatingDetail = true;
        try
        {
            SelectedRoom = selectedCode != null
                ? Rooms.FirstOrDefault(r => r.Code == selectedCode)
                : null;
            if (SelectedRoom is null && Rooms.Count > 0) SelectedRoom = Rooms[0];
        }
        finally { _updatingDetail = false; }

        RefreshRoomDetail();
    }

    partial void OnSelectedRoomChanged(RoomListItemViewModel? value)
    {
        if (!_updatingDetail) RefreshRoomDetail();
    }

    partial void OnSelectedMemberChanged(MemberListItemViewModel? value)
    {
        if (!_updatingDetail) RefreshRoomDetail();
    }

    private void RefreshRoomDetail()
    {
        var code = SelectedRoom?.Code;
        if (code is null)
        {
            IsRoomSelected = false;
            RoomCode = "";
            Members.Clear();
            TrayItems.Clear();
            TrayHeader = "";
            return;
        }

        IsRoomSelected = true;
        RoomCode = code;

        var selectedFp = SelectedMember?.Fingerprint; // null = 全部成员

        _updatingDetail = true;
        try
        {
            Members.Clear();
            var all = new MemberListItemViewModel(null, "全部成员", "显示所有文件");
            Members.Add(all);
            Members.Add(new MemberListItemViewModel(_settings.Fingerprint, $"{_settings.Alias} (我)", "本机"));
            foreach (var device in _discovery.GetDevices().Where(d => d.ContainsRoom(code)).OrderBy(d => d.Alias, StringComparer.OrdinalIgnoreCase))
            {
                Members.Add(new MemberListItemViewModel(
                    device.Fingerprint,
                    device.Alias,
                    $"{device.Endpoint} · {FormatLatency(device.Fingerprint)}"));
            }

            SelectedMember = Members.FirstOrDefault(m => m.Fingerprint == selectedFp) ?? all;

            var items = _room.GetVisibleItems(code);
            if (selectedFp != null) items = items.Where(i => i.OwnerFingerprint == selectedFp).ToList();

            TrayItems.Clear();
            foreach (var item in items)
            {
                TrayItems.Add(new TrayItemViewModel(item, _settings.Fingerprint, code, DownloadItemCommand, DeleteItemCommand));
            }
            TrayHeader = TrayItems.Count == 0
                ? "托盘 (空)"
                : $"托盘 ({TrayItems.Count}){(selectedFp != null ? " · 按成员筛选" : "")}";
        }
        finally { _updatingDetail = false; }
    }

    // ============================ 附近设备 / 文本 ============================

    [RelayCommand]
    private async Task SendTextAsync()
    {
        if (SelectedDevice is null)
        {
            StatusText = "请先在左侧选择一个设备";
            return;
        }

        var text = MessageInput.Trim();
        if (text.Length == 0)
        {
            StatusText = "请输入要发送的文本";
            return;
        }

        try
        {
            var target = SelectedDevice;
            await _transfer.SendTextAsync(target.Record, text);
            Messages.Add(MessageItemViewModel.Outgoing(target.Alias, text));
            MessageInput = "";
            StatusText = $"已发送给 {target.Alias}";
        }
        catch (Exception ex)
        {
            StatusText = $"发送失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveAlias()
    {
        var alias = AliasInput.Trim();
        if (alias.Length == 0)
        {
            StatusText = "昵称不能为空";
            return;
        }

        _settings.Alias = alias;
        _settings.Save();
        StatusText = $"昵称已保存为 {alias}(其他设备稍后可见)";
    }

    private void OnTextReceived(string alias, string text)
    {
        Messages.Add(MessageItemViewModel.Incoming(alias, text));
        StatusText = $"收到来自 {alias} 的文本";
    }

    private void OnFileReceived(string alias, string fileName, string savedPath)
    {
        Messages.Add(MessageItemViewModel.IncomingFile(alias, fileName, savedPath));
        StatusText = $"收到文件 {fileName}";
    }

    // ============================ 房间 / 托盘(分布式) ============================

    [RelayCommand]
    private void CreateRoom()
    {
        try
        {
            var code = _room.CreateRoom(null);
            StatusText = $"房间已创建: {code}";
        }
        catch (Exception ex)
        {
            StatusText = "创建房间失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private void JoinRoom()
    {
        var code = JoinCodeInput.Trim().ToUpperInvariant();
        if (code.Length != 8 || code.Any(c => c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9'))))
        {
            StatusText = "房间码格式不正确(8 位大写字母/数字)";
            return;
        }

        try
        {
            _room.CreateRoom(code);
            var peers = _discovery.GetDevices().Count(d => d.ContainsRoom(code));
            StatusText = peers > 0
                ? $"已加入房间 {code}(当前 {peers} 个其他在线节点)"
                : $"已加入房间 {code}(暂无其他在线节点,房间已保留在本地)";
            JoinCodeInput = "";
        }
        catch (Exception ex)
        {
            StatusText = "加入房间失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private void DeleteRoom()
    {
        var code = SelectedRoom?.Code;
        if (code is null) return;
        try
        {
            _room.DeleteRoom(code);
            StatusText = $"已从本机删除房间 {code}(其他节点不受影响)";
        }
        catch (Exception ex)
        {
            StatusText = "删除房间失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var code = SelectedRoom?.Code;
        if (code is null) return;
        var picker = PickFilesAsync;
        if (picker is null) return;

        var files = await picker();
        if (files.Count == 0) return;

        try
        {
            _room.AddFiles(code, files);
            StatusText = $"已放入 {files.Count} 个文件并同步到房间节点";
        }
        catch (Exception ex)
        {
            StatusText = "放入托盘失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DownloadItemAsync(TrayItemViewModel? item)
    {
        if (item is null) return;
        if (item.IsMine)
        {
            StatusText = "该文件就在本机: " + item.FilePath;
            return;
        }

        var picker = PickSaveFileAsync;
        if (picker is null) return;
        var target = await picker(item.FileName);
        if (string.IsNullOrEmpty(target)) return;

        try
        {
            await _room.DownloadItemAsync(item.RoomCode, item.Id, target);
            StatusText = $"已下载: {target}";
        }
        catch (Exception ex)
        {
            StatusText = "下载失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private void DeleteItem(TrayItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            _room.RemoveItem(item.RoomCode, item.Id);
            StatusText = $"已移除 {item.FileName} 并同步到房间节点";
        }
        catch (Exception ex)
        {
            StatusText = "移除失败: " + ex.Message;
        }
    }
}
