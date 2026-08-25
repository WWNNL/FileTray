using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FileTray.ViewModels;

namespace FileTray.Views;

/// <summary>
/// 弹出托盘窗口:具备主窗口房间页的全部功能(创建/加入/删除房间、成员二段点击、
/// 托盘文件的添加/下载/删除、拖入文件、房间码复制),状态反馈显示在左下角;
/// 无边框,可切换置顶。
/// </summary>
public partial class TrayPopupWindow : Window
{
    private MainWindowViewModel? _main;

    public TrayPopupWindow()
    {
        InitializeComponent();
        // DragOver 必须持续设置 DragEffects:未处理的 DragOver 会被平台判定为拒绝放置
        PopupRoot.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        PopupRoot.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        PopupRoot.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        PopupRoot.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void Bind(MainWindowViewModel main)
    {
        if (_main != null) return;
        _main = main;

        if (DataContext is TrayPopupViewModel vm)
        {
            vm.Attach(main);
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

            // 主窗口状态文本同步到小窗左下角
            main.StatusTextChanged += text => Dispatcher.UIThread.Post(() => vm.ShowStatus(text));
        }

        // 成员选中态变化时同步按钮高亮(与主窗口同款样式类)
        main.RoomsChangedForPopup += SyncMemberButtonHighlights;
    }

    /// <summary>把成员按钮的选中态同步为高亮样式类。</summary>
    private void SyncMemberButtonHighlights()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var button in PopupMemberItems.GetVisualDescendants().OfType<Button>())
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

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async void OnCopyRoomCode(object? sender, RoutedEventArgs e)
    {
        if (_main is { } main && !string.IsNullOrEmpty(main.RoomCode))
        {
            var clipboard = Clipboard;
            if (clipboard != null)
            {
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(main.RoomCode));
                await clipboard.SetDataAsync(data);
                main.StatusText = "房间码已复制到剪贴板";
            }
        }
    }

    /// <summary>成员按钮二段点击:未选中→选中筛选(高亮);已选中→复制最优 IP。</summary>
    private async void OnMemberClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberListItemViewModel member } || _main is not { } main)
        {
            return;
        }

        if (!member.IsSelected)
        {
            main.SelectMember(member);
            SyncMemberButtonHighlights();
            return;
        }

        if (member.Fingerprint is null)
        {
            main.StatusText = "「全部」不是具体成员,点击其他成员可复制其 IP";
            return;
        }

        var ip = main.ResolveDeviceIp(member.Fingerprint);
        if (ip is { } value)
        {
            var clipboard = Clipboard;
            if (clipboard != null)
            {
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(value));
                await clipboard.SetDataAsync(data);
                main.StatusText = $"已复制 {member.DisplayText} 的 IP: {value}(延迟最低)";
            }
        }
        else
        {
            main.StatusText = $"未能解析 {member.DisplayText} 的地址(设备可能离线)";
        }
    }

    /// <summary>横向滚动区:竖直滚轮转水平滚动(与主窗口同款逻辑)。</summary>
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

    // ============================ 拖入文件 ============================

    private static bool HasLocalFiles(DragEventArgs e)
        => e.DataTransfer.Contains(DataFormat.File);

    private void ShowDropOverlay(bool show)
    {
        var code = _main?.RoomCode;
        DropOverlayText.Text = string.IsNullOrEmpty(code)
            ? "尚未选择房间,先在主窗口选择房间"
            : $"松开鼠标把文件放入房间 {code}";
        DropOverlay.IsVisible = show;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var has = HasLocalFiles(e);
        e.DragEffects = has ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (has) ShowDropOverlay(true);
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

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        ShowDropOverlay(false);
        if (_main is not { } main) return;

        var items = e.DataTransfer.TryGetFiles();
        var paths = items?
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null)
            .Select(p => p!)
            .ToList() as IReadOnlyList<string>;

        if (paths is { Count: > 0 })
        {
            await main.HandleFilesDroppedAsync(paths);
        }
        else
        {
            main.StatusText = "只支持拖入本地文件";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 关闭按钮只隐藏,保留实例供下次弹出
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
