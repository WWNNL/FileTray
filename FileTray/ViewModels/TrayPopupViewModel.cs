using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FileTray.ViewModels;

/// <summary>
/// 弹出托盘窗口的视图模型:除房间列表切换外,具备主窗口房间页的全部功能——
/// 创建/加入/删除房间、成员二段点击(选中筛选/复制 IP)、托盘文件的添加/下载/删除、
/// 拖入文件、房间码复制;操作结果反馈到自己的 StatusText(小窗左下角)。
/// 数据与主窗口共享同一批成员/文件实例,选中态天然同步。
/// </summary>
public partial class TrayPopupViewModel : ViewModelBase
{
    private MainWindowViewModel? _main;

    /// <summary>由小窗注入的文件选择器(与主窗口同款)。</summary>
    public Func<System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>>>? PickFilesAsync { get; set; }

    /// <summary>由小窗注入的保存路径选择器(与主窗口同款)。</summary>
    public Func<string, System.Threading.Tasks.Task<string?>>? PickSaveFileAsync { get; set; }

    [ObservableProperty] private bool _hasRoom;
    [ObservableProperty] private string _roomCode = "";
    [ObservableProperty] private string _trayHeader = "";
    [ObservableProperty] private bool _pinTopmost;
    [ObservableProperty] private string _pinText = "置顶: 关";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _joinCodeInput = "";

    public ObservableCollection<MemberListItemViewModel> Members { get; } = new();
    public ObservableCollection<TrayItemViewModel> TrayItems { get; } = new();

    public TrayPopupViewModel()
    {
    }

    public void Attach(MainWindowViewModel main)
    {
        if (_main != null) return;
        _main = main;
        main.RoomsChangedForPopup += () => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    partial void OnPinTopmostChanged(bool value)
        => PinText = value ? "置顶: 开" : "置顶: 关";

    private void Refresh()
    {
        var main = _main;
        if (main is null) return;

        var code = main.SelectedRoom?.Code;
        HasRoom = code != null;
        RoomCode = code ?? "";

        Members.Clear();
        TrayItems.Clear();

        if (code is null) return;

        // 成员与托盘条目直接镜像主窗口(共享实例:选中态/命令天然同步)
        foreach (var member in main.Members) Members.Add(member);
        foreach (var item in main.TrayItems) TrayItems.Add(item);

        TrayHeader = main.TrayItems.Count == 0 ? "托盘 (空)" : main.TrayHeader;
    }

    // ============================ 状态提示(与主窗口同源) ============================

    /// <summary>主窗口状态变化同步到小窗左下角(主 VM 写 StatusText 时由小窗转发)。</summary>
    public void ShowStatus(string text) => StatusText = text;

    private void RunWithStatus(string doing, string done, Action action)
    {
        try
        {
            action();
            StatusText = done;
        }
        catch (Exception ex)
        {
            StatusText = $"{doing}失败: {ex.Message}";
        }
    }

    // ============================ 房间操作 ============================

    [RelayCommand]
    private void CreateRoom() => RunWithStatus("创建房间", "房间已创建", () => _main?.CreateRoomCommand.Execute(null));

    [RelayCommand]
    private void JoinRoom()
    {
        var code = JoinCodeInput.Trim().ToUpperInvariant();
        if (code.Length != 8 || code.Any(c => c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9'))))
        {
            StatusText = "房间码格式不正确(8 位大写字母/数字)";
            return;
        }
        RunWithStatus("加入房间", $"已加入房间 {code}", () =>
        {
            _main?.JoinRoomWithCode(code);
            JoinCodeInput = "";
        });
    }

    [RelayCommand]
    private void DeleteRoom() => RunWithStatus("删除房间", "已从本机删除房间", () => _main?.DeleteRoomCommand.Execute(null));

    [RelayCommand]
    private async System.Threading.Tasks.Task AddFilesAsync()
    {
        if (_main is null || PickFilesAsync is null) return;
        var files = await PickFilesAsync();
        if (files.Count == 0) return;
        await _main.HandleFilesDroppedAsync(files);
    }

    /// <summary>删除托盘条目(转发主 VM,保持同步)。</summary>
    [RelayCommand]
    private void DeleteItem(TrayItemViewModel? item)
    {
        if (item is null || _main is null) return;
        RunWithStatus("移除", $"已移除 {item.FileName}", () => _main.DeleteItemCommand.Execute(item));
    }

    /// <summary>下载托盘条目(转发主 VM,保存路径选择走小窗自己的选择器,续传时不弹窗;
    /// 结果反馈由主 VM 的 StatusText 同步到小窗左下角)。</summary>
    [RelayCommand]
    private System.Threading.Tasks.Task DownloadItemAsync(TrayItemViewModel? item)
    {
        if (item is null || _main is null) return System.Threading.Tasks.Task.CompletedTask;
        return _main.RequestDownloadAsync(item, PickSaveFileAsync ?? _main.PickSaveFileAsync);
    }
}
