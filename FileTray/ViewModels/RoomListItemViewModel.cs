using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileTray.ViewModels;

/// <summary>房间列表条目。</summary>
public partial class RoomListItemViewModel : ViewModelBase
{
    public string Code { get; }

    [ObservableProperty] private string _summaryText = "";

    public RoomListItemViewModel(string code, string summaryText)
    {
        Code = code;
        _summaryText = summaryText;
    }
}

/// <summary>
/// 房间成员条目(紧凑筛选按钮)。Fingerprint 为 null 表示"全部成员"(不筛选);
/// 本机成员的 Fingerprint 为自己的指纹。同一时间只有一个 IsSelected。
/// </summary>
public partial class MemberListItemViewModel : ViewModelBase
{
    private readonly Action<MemberListItemViewModel>? _onSelected;

    public string? Fingerprint { get; }

    [ObservableProperty] private string _displayText = "";
    [ObservableProperty] private bool _isSelected;

    public MemberListItemViewModel(string? fingerprint, string displayText, Action<MemberListItemViewModel>? onSelected = null)
    {
        Fingerprint = fingerprint;
        _displayText = displayText;
        _onSelected = onSelected;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        // 用户点击选中时通知主 VM 做互斥与筛选;程序性刷新直接赋值不经过用户事件,
        // 但 TwoWay 绑定也会走这里,靠 _onSelected 内部判断幂等
        if (value) _onSelected?.Invoke(this);
    }
}
