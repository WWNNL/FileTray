using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FileTray.Models;

namespace FileTray.ViewModels;

public partial class DeviceListItemViewModel : ViewModelBase
{
    public DeviceRecord Record { get; private set; }

    public string Fingerprint => Record.Fingerprint;

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _roomBadge = "";
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private bool _hasRoom;

    public DeviceListItemViewModel(DeviceRecord record)
    {
        Record = record;
        Update(record);
    }

    public void Update(DeviceRecord record)
    {
        Record = record;
        Alias = record.Alias;
        Endpoint = record.Endpoint;
        RoomBadge = record.Rooms.Count > 0 ? "房间 " + string.Join("、", record.Rooms) : "";
        HasRoom = record.Rooms.Count > 0;
    }
}
