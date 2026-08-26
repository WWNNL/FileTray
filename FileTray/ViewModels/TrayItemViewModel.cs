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

    /// <summary>正在下载或已暂停(条目上显示进度条;结束恢复隐藏)。</summary>
    [ObservableProperty] private bool _isDownloading;
    /// <summary>已暂停(显示「继续」按钮而非「暂停」)。</summary>
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _showDownloadButton;
    [ObservableProperty] private bool _showPauseButton;
    [ObservableProperty] private bool _showResumeButton;
    /// <summary>下载百分比 0~100;总大小未知时为 -1(进度条转为不确定动画)。</summary>
    [ObservableProperty] private int _downloadPercent = -1;
    [ObservableProperty] private bool _downloadIndeterminate;
    /// <summary>进度文本,如 "42% · 10.5 MB / 25.0 MB"。</summary>
    [ObservableProperty] private string _downloadText = "";

    public IRelayCommand DownloadCommand { get; }
    public IRelayCommand PauseDownloadCommand { get; }
    public IRelayCommand ResumeDownloadCommand { get; }
    public IRelayCommand CancelDownloadCommand { get; }
    public IRelayCommand DeleteCommand { get; }

    public TrayItemViewModel(TrayItemDto item, string selfFingerprint, string roomCode,
        IRelayCommand downloadCommand, IRelayCommand pauseCommand, IRelayCommand resumeCommand,
        IRelayCommand cancelCommand, IRelayCommand deleteCommand)
    {
        Id = item.Id;
        FilePath = item.FilePath;
        RoomCode = roomCode;
        IsMine = item.OwnerFingerprint == selfFingerprint;
        _fileName = item.FileName;
        _metaLine = $"{item.OwnerAlias}{(IsMine ? "(我)" : "")} · {FormatSize(item.FileSize)} · {FormatTime(item.AddedAt)}";
        _showDownloadButton = !IsMine;
        DownloadCommand = downloadCommand;
        PauseDownloadCommand = pauseCommand;
        ResumeDownloadCommand = resumeCommand;
        CancelDownloadCommand = cancelCommand;
        DeleteCommand = deleteCommand;
    }

    /// <summary>更新下载进度显示(条目 VM 每次刷新会重建,进度真值存于主 VM 的会话表)。</summary>
    public void ApplyDownloadProgress(int percent, long received, long total, bool paused = false)
    {
        IsDownloading = true;
        IsPaused = paused;
        ShowDownloadButton = false;
        ShowPauseButton = !paused;
        ShowResumeButton = paused;
        DownloadPercent = percent;
        DownloadIndeterminate = percent < 0;
        var text = percent >= 0
            ? $"{percent}% · {FormatSize(received)} / {FormatSize(total)}"
            : $"{FormatSize(received)} / 大小未知";
        DownloadText = paused ? "已暂停 · " + text : text;
    }

    /// <summary>下载结束(成功、取消或失败)清除进度显示。</summary>
    public void ClearDownloadProgress()
    {
        IsDownloading = false;
        IsPaused = false;
        ShowDownloadButton = !IsMine;
        ShowPauseButton = false;
        ShowResumeButton = false;
        DownloadText = "";
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
