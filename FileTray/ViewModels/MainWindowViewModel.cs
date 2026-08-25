using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    private bool _suppressDetailRefresh;
    private bool _applyingMemberSelection;
    private string? _pendingMemberFilter; // 成员筛选(指纹,null=全部),刷新后据此更新托盘视图

    /// <summary>下载进度真值(条目 Id → 进度);条目 VM 每次刷新重建,进度在重建时回填。</summary>
    private readonly Dictionary<string, (int Percent, long Received, long Total)> _downloadProgress = new();
    /// <summary>正在下载的条目 Id(防重复点击)。</summary>
    private readonly HashSet<string> _downloadingItems = new();

    /// <summary>由 MainWindow 在打开后注入的文件选择器(打开多个文件)。</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFilesAsync { get; set; }

    /// <summary>由 MainWindow 在打开后注入的保存路径选择器。</summary>
    public Func<string, Task<string?>>? PickSaveFileAsync { get; set; }

    /// <summary>由 MainWindow 注入:复制文本到剪贴板。</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    /// <summary>弹出托盘窗口订阅的刷新信号(与 UI 刷新同源)。</summary>
    public event Action? RoomsChangedForPopup;

    /// <summary>状态文本变化(弹出小窗同步显示到其左下角)。</summary>
    public event Action<string>? StatusTextChanged;

    partial void OnStatusTextChanged(string value) => StatusTextChanged?.Invoke(value);

    public string SelfFingerprint => _settings.Fingerprint;
    public string SelfAlias => _settings.Alias;

    /// <summary>取维护指定房间的在线节点列表(弹出窗口用)。</summary>
    public IReadOnlyList<DeviceRecord> PopupRoomDevices(string code)
        => _discovery.GetDevices().Where(d => d.ContainsRoom(code)).OrderBy(d => d.Alias, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>按指纹解析设备当前最优 IP(无则 null)。</summary>
    public string? ResolveDeviceIp(string fingerprint)
        => _discovery.GetDevices().FirstOrDefault(d => d.Fingerprint == fingerprint)?.BestEndpoint()?.Ip;

    /// <summary>由 MainWindow 注入:用户把文件拖入窗口时调用(路径列表)。</summary>
    public Func<IReadOnlyList<string>, Task>? FilesDroppedAsync { get; set; }

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
        RoomsChangedForPopup?.Invoke();
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
                var vm = new DeviceListItemViewModel(record)
                {
                    LatencyText = FormatLatency(record.Fingerprint),
                    CopyIpRequested = OnCopyIpRequested,
                };
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

    private async void OnCopyIpRequested(string ip)
    {
        try
        {
            if (CopyToClipboardAsync != null)
            {
                await CopyToClipboardAsync(ip);
            }
            StatusText = $"已复制 IP: {ip}";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败: {ex.Message}";
        }
    }

    private void RefreshRooms()
    {
        var summaries = _room.GetRoomSummaries();
        var devices = _discovery.GetDevices();
        var selectedCode = SelectedRoom?.Code;

        for (var i = Rooms.Count - 1; i >= 0; i--)
        {
            if (summaries.All(s => s.Code != Rooms[i].Code)) Rooms.RemoveAt(i);
        }

        foreach (var summary in summaries)
        {
            var nodes = devices.Count(d => d.ContainsRoom(summary.Code)) + 1; // + 本机
            var summaryText = $"{summary.ItemCount} 个文件 · {nodes} 个节点";
            var existing = Rooms.FirstOrDefault(r => r.Code == summary.Code);
            if (existing is null) Rooms.Add(new RoomListItemViewModel(summary.Code, summaryText));
            else existing.SummaryText = summaryText;
        }
        HasRooms = Rooms.Count > 0;

        // 保持选中房间:实例优先复用,房间没了才回落到第一个
        if (selectedCode != null && Rooms.Any(r => r.Code == selectedCode))
        {
            if (SelectedRoom?.Code != selectedCode)
            {
                _suppressDetailRefresh = true;
                SelectedRoom = Rooms.First(r => r.Code == selectedCode);
                _suppressDetailRefresh = false;
            }
        }
        else if (SelectedRoom is null && Rooms.Count > 0)
        {
            _suppressDetailRefresh = true;
            SelectedRoom = Rooms[0];
            _suppressDetailRefresh = false;
        }

        RefreshRoomDetail();
    }

    partial void OnSelectedRoomChanged(RoomListItemViewModel? value)
    {
        if (!_suppressDetailRefresh)
        {
            _pendingMemberFilter = null; // 切换房间时重置成员筛选
            RefreshRoomDetail();
        }
    }

    /// <summary>成员筛选按钮被点击(互斥单选):更新筛选并刷新托盘。</summary>
    private void OnMemberSelected(MemberListItemViewModel member)
    {
        if (_applyingMemberSelection)
        {
            return; // 程序性刷新赋值,不是用户点击
        }

        SelectMember(member);
    }

    /// <summary>选中某个成员并按其筛选托盘(用户点击/弹窗联动共用)。</summary>
    public void SelectMember(MemberListItemViewModel member)
    {
        _applyingMemberSelection = true;
        try
        {
            foreach (var m in Members)
            {
                m._applyingProgrammatic = true;
                m.IsSelected = ReferenceEquals(m, member);
                m._applyingProgrammatic = false;
            }
        }
        finally
        {
            _applyingMemberSelection = false;
        }

        _pendingMemberFilter = member.Fingerprint;
        RefreshTrayItems();
    }

    /// <summary>刷新房间详情:成员做增量更新(复用同指纹实例,选中状态不丢失),托盘按当前筛选刷新。</summary>
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

        // ---- 成员:增量更新,实例稳定;显示文本 = 名称(延迟) ----
        var devices = _discovery.GetDevices().Where(d => d.ContainsRoom(code)).OrderBy(d => d.Alias, StringComparer.OrdinalIgnoreCase).ToList();

        if (Members.Count == 0 || Members[0].Fingerprint != null)
        {
            Members.Insert(0, new MemberListItemViewModel(null, "全部", OnMemberSelected));
        }
        if (Members.Count < 2 || Members[1].Fingerprint != _settings.Fingerprint)
        {
            Members.Insert(1, new MemberListItemViewModel(_settings.Fingerprint, $"{_settings.Alias}(我)", OnMemberSelected));
        }
        Members[0].DisplayText = "全部";
        Members[1].DisplayText = $"{_settings.Alias}(我)";

        var onlineFingerprints = devices.Select(d => d.Fingerprint).ToHashSet();
        for (var i = Members.Count - 1; i >= 2; i--)
        {
            if (!onlineFingerprints.Contains(Members[i].Fingerprint!)) Members.RemoveAt(i);
        }

        foreach (var device in devices)
        {
            var existing = Members.FirstOrDefault(m => m.Fingerprint == device.Fingerprint);
            if (existing is null)
            {
                Members.Add(new MemberListItemViewModel(device.Fingerprint, "", OnMemberSelected));
            }
            existing = Members.FirstOrDefault(m => m.Fingerprint == device.Fingerprint);
            existing!.DisplayText = $"{device.Alias}({FormatLatency(device.Fingerprint)})";
        }

        // 选中的成员离线后回落到"全部"
        if (_pendingMemberFilter != null && Members.All(m => m.Fingerprint != _pendingMemberFilter))
        {
            _pendingMemberFilter = null;
        }

        _applyingMemberSelection = true;
        try
        {
            foreach (var m in Members)
            {
                m._applyingProgrammatic = true;
                m.IsSelected = m.Fingerprint == _pendingMemberFilter || (m.Fingerprint == null && _pendingMemberFilter == null);
                m._applyingProgrammatic = false;
            }
        }
        finally
        {
            _applyingMemberSelection = false;
        }

        RefreshTrayItems();
    }

    private void RefreshTrayItems()
    {
        var code = SelectedRoom?.Code;
        if (code is null)
        {
            TrayItems.Clear();
            TrayHeader = "";
            return;
        }

        var filterFp = _pendingMemberFilter;
        var items = _room.GetVisibleItems(code);
        if (filterFp != null) items = items.Where(i => i.OwnerFingerprint == filterFp).ToList();

        TrayItems.Clear();
        foreach (var item in items)
        {
            var vm = new TrayItemViewModel(item, _settings.Fingerprint, code, DownloadItemCommand, DeleteItemCommand);
            // 下载中的条目在刷新重建后回填进度显示
            if (_downloadProgress.TryGetValue(item.Id, out var p)) vm.ApplyDownloadProgress(p.Percent, p.Received, p.Total);
            TrayItems.Add(vm);
        }
        TrayHeader = TrayItems.Count == 0
            ? "托盘 (空)"
            : $"托盘 ({TrayItems.Count}){(filterFp != null ? " · 按成员筛选" : "")}";
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
        JoinRoomWithCode(code);
    }

    /// <summary>加入指定房间(主窗口与小窗共用)。</summary>
    public void JoinRoomWithCode(string code)
    {
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

        await AddFilesToRoomAsync(code, files);
    }

    /// <summary>把一组本地文件放入指定房间的托盘(拖拽与文件选择器共用)。</summary>
    private async Task AddFilesToRoomAsync(string code, IReadOnlyList<string> paths)
    {
        try
        {
            await Task.Run(() => _room.AddFiles(code, paths));
            StatusText = $"已放入 {paths.Count} 个文件并同步到房间节点";
        }
        catch (Exception ex)
        {
            StatusText = "放入托盘失败: " + ex.Message;
        }
    }

    /// <summary>拖拽入口:放入当前选中房间;未选中房间时提示。</summary>
    public async Task HandleFilesDroppedAsync(IReadOnlyList<string> paths)
    {
        var code = SelectedRoom?.Code;
        if (code is null)
        {
            StatusText = "请先创建或选择一个房间,再拖入文件";
            return;
        }

        var valid = paths.Where(File.Exists).Select(p => p).ToList();
        if (valid.Count == 0)
        {
            StatusText = "拖入的内容不包含本地文件";
            return;
        }

        await AddFilesToRoomAsync(code, valid);
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

        await DownloadTrayItemAsync(item.RoomCode, item.Id, target);
    }

    /// <summary>下载托盘文件到指定路径(主窗口与小窗共用);进度实时反映到条目 UI。</summary>
    public async Task DownloadTrayItemAsync(string roomCode, string itemId, string target)
    {
        if (!_downloadingItems.Add(itemId))
        {
            StatusText = "该文件正在下载中…";
            return;
        }

        _downloadProgress[itemId] = (0, 0, -1); // 先给不确定进度,拿到 Content-Length 后更新
        ApplyItemProgress(itemId);
        try
        {
            // Progress<T> 在 UI 线程构造,回调自动回到 UI 线程
            var progress = new Progress<(int Percent, long Received, long Total)>(p =>
            {
                _downloadProgress[itemId] = p;
                ApplyItemProgress(itemId);
            });
            await _room.DownloadItemAsync(roomCode, itemId, target, progress);
            StatusText = $"已下载: {target}";
        }
        catch (Exception ex)
        {
            StatusText = $"下载失败: {ex.Message}";
        }
        finally
        {
            _downloadingItems.Remove(itemId);
            _downloadProgress.Remove(itemId);
            ClearItemProgress(itemId);
        }
    }

    /// <summary>把进度字典里的当前值写到列表中该条目的活实例上(主窗与小窗共享实例)。</summary>
    private void ApplyItemProgress(string itemId)
    {
        if (!_downloadProgress.TryGetValue(itemId, out var p)) return;
        foreach (var vm in TrayItems)
        {
            if (vm.Id == itemId)
            {
                vm.ApplyDownloadProgress(p.Percent, p.Received, p.Total);
                return;
            }
        }
    }

    private void ClearItemProgress(string itemId)
    {
        foreach (var vm in TrayItems)
        {
            if (vm.Id == itemId)
            {
                vm.ClearDownloadProgress();
                return;
            }
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
