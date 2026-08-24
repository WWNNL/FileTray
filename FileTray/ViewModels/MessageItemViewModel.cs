using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileTray.ViewModels;

public partial class MessageItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _line = "";

    private MessageItemViewModel(string timeText, string line)
    {
        _timeText = timeText;
        _line = line;
    }

    public static MessageItemViewModel Incoming(string alias, string text)
        => new(DateTime.Now.ToString("HH:mm:ss"), $"{alias}: {text}");

    public static MessageItemViewModel Outgoing(string alias, string text)
        => new(DateTime.Now.ToString("HH:mm:ss"), $"我 → {alias}: {text}");

    public static MessageItemViewModel IncomingFile(string alias, string fileName, string savedPath)
        => new(DateTime.Now.ToString("HH:mm:ss"), $"{alias} 发来文件 {fileName},已保存到 {savedPath}");
}
