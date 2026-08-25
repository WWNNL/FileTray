using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FileTray.ViewModels;

namespace FileTray.Views;

public partial class MainWindow : Window
{
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

        // delta.Y 是"行数"刻度(通常 ±0.1~±3),乘一个像素系数得到顺手的滚动距离
        var offset = scroller.Offset;
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;

        var target = Math.Clamp(offset.X - delta * 40, 0, scroller.Extent.Width - scroller.Viewport.Width);
        scroller.Offset = new Vector(target, offset.Y);
        e.Handled = true;
    }

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
        // DragOver 持续触发,保持效果与遮罩状态即可
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
