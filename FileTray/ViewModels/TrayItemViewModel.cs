using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTray.Models;

namespace FileTray.ViewModels;

public partial class TrayItemViewModel : ViewModelBase
{
    public string Id { get; }
    public string FilePath { get; }
    public string RoomCode { get; }
    public bool IsMine { get; }

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _metaLine = "";

    public IRelayCommand DownloadCommand { get; }
    public IRelayCommand DeleteCommand { get; }

    public TrayItemViewModel(TrayItemDto item, string selfFingerprint, string roomCode, IRelayCommand downloadCommand, IRelayCommand deleteCommand)
    {
        Id = item.Id;
        FilePath = item.FilePath;
        RoomCode = roomCode;
        IsMine = item.OwnerFingerprint == selfFingerprint;
        _fileName = item.FileName;
        _metaLine = $"{item.OwnerAlias}{(IsMine ? "(我)" : "")} · {FormatSize(item.FileSize)} · {FormatTime(item.AddedAt)}";
        DownloadCommand = downloadCommand;
        DeleteCommand = deleteCommand;
    }

    private static string FormatTime(long unixMilliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).LocalDateTime.ToString("MM-dd HH:mm");

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };
}
