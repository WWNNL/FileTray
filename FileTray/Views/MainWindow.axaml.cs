using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FileTray.ViewModels;

namespace FileTray.Views;

public partial class MainWindow : Window
{
    private TrayPopupWindow? _trayPopup;

    public MainWindow()
    {
        InitializeComponent();
        RootGrid.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        RootGrid.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        RootGrid.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        RootGrid.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.RoomsChangedForPopup += SyncMemberButtonHighlights;
            vm.PickFilesAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择要放入托盘的文件",
                    AllowMultiple = true,
                });
                return files
                    .Select(f => f.TryGetLocalPath())
                    .Where(p => p != null)
                    .Select(p => p!)
                    .ToList();
            };

            vm.PickSaveFileAsync = async suggested =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "保存文件",
                    SuggestedFileName = suggested,
                });
                return file?.TryGetLocalPath();
            };

            vm.CopyToClipboardAsync = async text =>
            {
                var clipboard = Clipboard;
                if (clipboard is null) return;
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(text));
                await clipboard.SetDataAsync(data);
            };
        }
    }

    // ============================ 弹出托盘窗口 ============================

    /// <summary>成员筛选按钮:未选中→选中并筛选;已选中→复制该成员最优 IP。</summary>
    private async void OnMemberFilterClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberListItemViewModel member } button) return;
        if (DataContext is not MainWindowViewModel vm) return;

        if (member.IsSelected)
        {
            // 再次点击:复制最优 IP(本机成员提示无 IP)
            var ip = member.Fingerprint is { } fp ? vm.ResolveDeviceIp(fp) : null;
            if (ip is { } value && vm.CopyToClipboardAsync != null)
            {
                await vm.CopyToClipboardAsync(value);
                vm.StatusText = $"已复制 {member.DisplayText} 的 IP: {value}(延迟最低)";
            }
            else
            {
                vm.StatusText = member.Fingerprint == null
                    ? "「全部」无 IP"
                    : "该成员暂无可用 IP(可能刚离线)";
            }
            return;
        }

        vm.SelectMember(member);
        SyncMemberButtonHighlights();
    }

    /// <summary>把成员按钮的选中态同步为高亮样式类(遍历可视树内的成员按钮)。</summary>
    private void SyncMemberButtonHighlights()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var button in MemberFilterItems.GetVisualDescendants().OfType<Button>())
            {
                if (button.Tag is MemberListItemViewModel m)
                {
                    var on = m.IsSelected;
                    if (on && !button.Classes.Contains("memberSelected")) button.Classes.Add("memberSelected");
                    if (!on && button.Classes.Contains("memberSelected")) button.Classes.Remove("memberSelected");
                }
            }
        });
    }

    /// <summary>右上角"托盘"按钮:在按钮下方弹出精简托盘窗口。</summary>
    private void OnShowTrayPopup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        _trayPopup ??= new TrayPopupWindow
        {
            DataContext = new TrayPopupViewModel(),
        };
        _trayPopup.Bind(vm);

        if (_trayPopup.IsVisible)
        {
            _trayPopup.Hide();
            return;
        }

        // 弹出位置:按钮正下方,屏幕边界内收缩
        if (sender is Button button)
        {
            var topLeft = button.PointToScreen(new Avalonia.Point(0, button.Bounds.Height + 6));
            var x = Math.Min(topLeft.X, Screens.Primary?.WorkingArea.Width - 350 ?? topLeft.X);
            _trayPopup.Position = topLeft.WithX(x);
        }
        _trayPopup.Show();
        _trayPopup.Activate();
    }

    // ============================ 拖拽 ============================

    private static bool HasLocalFiles(DragEventArgs e)
        => e.DataTransfer.Contains(DataFormat.File);

    private void ShowDropOverlay(bool show)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            DropOverlayText.Text = show && vm.IsRoomSelected
                ? $"松开鼠标把文件放入房间 {vm.RoomCode}"
                : "松开鼠标把文件放入托盘";
            DropOverlaySubText.Text = show && !vm.IsRoomSelected
                ? "尚未选择房间,放入后会进入当前选中的房间"
                : "";
        }
        DropOverlay.IsVisible = show;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasLocalFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        ShowDropOverlay(HasLocalFiles(e));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasLocalFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        ShowDropOverlay(false);
    }

    /// <summary>把拖入的本地文件放入当前选中房间的托盘。</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        ShowDropOverlay(false);
        if (DataContext is not MainWindowViewModel vm) return;

        var items = e.DataTransfer.TryGetFiles();
        var paths = items?
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null)
            .Select(p => p!)
            .ToList() as IReadOnlyList<string>;

        if (paths is { Count: > 0 })
        {
            await vm.HandleFilesDroppedAsync(paths);
        }
        else
        {
            vm.StatusText = "只支持拖入本地文件";
        }
    }

    // ============================ 其他 ============================

    /// <summary>设备地址按钮:点击复制该 IP。</summary>
    private async void OnCopyDeviceIp(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceEndpointViewModel endpoint }
            && DataContext is MainWindowViewModel vm
            && vm.CopyToClipboardAsync != null)
        {
            await vm.CopyToClipboardAsync(endpoint.Ip);
            vm.StatusText = $"已复制 IP: {endpoint.Ip}";
        }
    }

    /// <summary>横向滚动区:把竖直滚轮换算成水平滚动(Shift+滚轮也保持水平)。</summary>
    private void OnHorizontalScrollerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;

        var offset = scroller.Offset;
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;

        var target = Math.Clamp(offset.X - delta * 40, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width));
        scroller.Offset = new Avalonia.Vector(target, offset.Y);
        e.Handled = true;
    }

    private async void OnCopyRoomCode(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && !string.IsNullOrEmpty(vm.RoomCode))
        {
            var clipboard = Clipboard;
            if (clipboard != null)
            {
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(vm.RoomCode));
                await clipboard.SetDataAsync(data);
                vm.StatusText = "房间码已复制到剪贴板";
            }
        }
    }
}
