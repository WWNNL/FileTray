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
/// 房间成员条目。Fingerprint 为 null 表示"全部成员"(不筛选);
/// 本机成员的 Fingerprint 为自己的指纹。
/// </summary>
public partial class MemberListItemViewModel : ViewModelBase
{
    public string? Fingerprint { get; }

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _detailText = "";

    public MemberListItemViewModel(string? fingerprint, string displayName, string detailText)
    {
        Fingerprint = fingerprint;
        _displayName = displayName;
        _detailText = detailText;
    }
}
