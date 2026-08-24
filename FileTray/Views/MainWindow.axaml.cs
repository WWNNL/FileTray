using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
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
        }
    }

    /// <summary>拖拽悬停:携带文件时显示"可复制放置"光标。</summary>
    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>把拖入的本地文件放入当前选中房间的托盘。</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
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
