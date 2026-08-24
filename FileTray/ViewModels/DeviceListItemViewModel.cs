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
    [ObservableProperty] private string _latencyText = "—";

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
    }
}
