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
        // UI 在 PropertyChanged 里同步高亮样式类;用户点击路径由 _onSelected 处理互斥
        IsSelectedChanged?.Invoke(this, value);
        if (value && !_applyingProgrammatic) _onSelected?.Invoke(this);
    }

    /// <summary>程序性赋值期间抑制用户点击回调(防循环)。</summary>
    internal bool _applyingProgrammatic;

    /// <summary>选中状态变化(UI 同步高亮用)。</summary>
    internal event Action<MemberListItemViewModel, bool>? IsSelectedChanged;
}
