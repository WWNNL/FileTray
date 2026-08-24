using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    [ObservableProperty] private bool _isInRoom;
    [ObservableProperty] private string _roomCode = "";
    [ObservableProperty] private string _roleText = "";
    [ObservableProperty] private string _memberSummary = "";
    [ObservableProperty] private string _joinCodeInput = "";

    public ObservableCollection<DeviceListItemViewModel> Devices { get; } = new();
    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();
    public ObservableCollection<TrayItemViewModel> TrayItems { get; } = new();

    public MainWindowViewModel(
        SettingsService settings,
        DiscoveryService discovery,
        HttpApiService server,
        RoomService room,
        TransferService transfer)
    {
        _settings = settings;
        _discovery = discovery;
        _server = server;
        _room = room;
        _transfer = transfer;
        _aliasInput = settings.Alias;

        _discovery.DevicesChanged += () => Dispatcher.UIThread.Post(RefreshDevices);
        _room.RoomStateChanged += () => Dispatcher.UIThread.Post(RefreshRoom);
        _room.RoomClosed += reason => Dispatcher.UIThread.Post(() =>
        {
            RefreshRoom();
            StatusText = $"已退出房间: {reason}";
        });
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
            if (existing is null) Devices.Add(new DeviceListItemViewModel(record));
            else existing.Update(record);
        }

        DevicesHeader = $"附近设备 ({Devices.Count})";
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

    // ============================ 房间 / 托盘 ============================

    [RelayCommand]
    private void CreateRoom()
    {
        if (_room.IsInRoom) return;
        try
        {
            _room.CreateRoom();
            RefreshRoom();
            StatusText = $"房间已创建,房间码 {_room.Code}";
        }
        catch (Exception ex)
        {
            StatusText = "创建房间失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (_room.IsInRoom) return;
        var code = JoinCodeInput.Trim().ToUpperInvariant();
        if (code.Length != 8 || code.Any(c => c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9'))))
        {
            StatusText = "房间码格式不正确(8 位大写字母/数字)";
            return;
        }

        try
        {
            StatusText = $"正在加入房间 {code}…";
            await _room.JoinRoomAsync(code);
            JoinCodeInput = "";
            StatusText = $"已加入房间 {code}";
        }
        catch (Exception ex)
        {
            StatusText = "加入房间失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        try
        {
            await _room.LeaveRoomAsync();
            StatusText = "已离开房间";
        }
        catch (Exception ex)
        {
            StatusText = "离开房间失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (!_room.IsInRoom) return;
        var picker = PickFilesAsync;
        if (picker is null) return;

        var files = await picker();
        if (files.Count == 0) return;

        try
        {
            await _room.AddFilesAsync(files);
            StatusText = $"已放入 {files.Count} 个文件";
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
            await _room.DownloadItemAsync(item.Id, target);
            StatusText = $"已下载: {target}";
        }
        catch (Exception ex)
        {
            StatusText = "下载失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteItemAsync(TrayItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            await _room.RemoveItemAsync(item.Id);
            StatusText = $"已移除 {item.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = "移除失败: " + ex.Message;
        }
    }

    private void RefreshRoom()
    {
        var state = _room.State;
        if (!_room.IsInRoom || state is null)
        {
            IsInRoom = false;
            RoomCode = "";
            RoleText = "";
            MemberSummary = "";
            TrayItems.Clear();
            return;
        }

        IsInRoom = true;
        RoomCode = state.Code;
        RoleText = _room.Role == RoomRole.Host ? "房主" : "成员";
        MemberSummary = "成员: " + string.Join("、", state.Members
            .Select(m => m.Alias + (m.Fingerprint == state.HostFingerprint ? "(房主)" : "")));
        TrayItems.Clear();
        foreach (var item in state.Tray)
        {
            TrayItems.Add(new TrayItemViewModel(item, _settings.Fingerprint, DownloadItemCommand, DeleteItemCommand));
        }
    }
}
