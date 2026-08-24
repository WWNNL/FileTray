using System;
using System.IO;
using System.Threading.Tasks;
using FileTray.ViewModels;

namespace FileTray.Services;

/// <summary>组合根:创建并持有全部服务与主 ViewModel。</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public SettingsService Settings { get; private set; } = null!;
    public DiscoveryService Discovery { get; private set; } = null!;
    public RoomService Room { get; private set; } = null!;
    public HttpApiService Server { get; private set; } = null!;
    public TransferService Transfer { get; private set; } = null!;
    public LatencyService Latency { get; private set; } = null!;
    public MainWindowViewModel MainVm { get; private set; } = null!;

    private AppServices()
    {
    }

    public void Initialize()
    {
        var dataDir = CliOptions.DataDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileTray");

        Log.Init(Path.Combine(dataDir, "log.txt"));

        Settings = new SettingsService(dataDir);
        if (CliOptions.Alias != null)
        {
            Settings.Alias = CliOptions.Alias;
            Settings.Save();
        }

        Discovery = new DiscoveryService();
        Room = new RoomService(Settings, Discovery);
        Server = new HttpApiService(Discovery, Room, () => Transfer?.SelfInfo() ?? new Models.DeviceInfoDto());
        Transfer = new TransferService(Settings, Server, () => Room.RoomCodes);
        Latency = new LatencyService(Discovery);

        MainVm = new MainWindowViewModel(Settings, Discovery, Server, Room, Transfer, Latency);
    }

    public async Task StartupAsync()
    {
        try
        {
            await Server.StartAsync(Settings.Port).ConfigureAwait(false);
            Discovery.Start(
                aliasProvider: () => Settings.Alias,
                fingerprintProvider: () => Settings.Fingerprint,
                portProvider: () => Server.Port,
                roomCodesProvider: () => Room.RoomCodes);
            Room.Start();
            Latency.Start();

            Log.Info($"启动完成: 别名={Settings.Alias} 指纹={Short(Settings.Fingerprint)} HTTP端口={Server.Port}");

            Avalonia.Threading.Dispatcher.UIThread.Post(() => MainVm.NotifyStarted(Server.Port));

            RunCliActions();
        }
        catch (Exception ex)
        {
            Log.Error($"启动失败: {ex}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => MainVm.NotifyStartupFailed(ex.Message));
        }
    }

    private void RunCliActions()
    {
        try
        {
            var roomCode = CliOptions.CreateRoomCode != null
                ? Room.CreateRoom(CliOptions.CreateRoomCode)
                : CliOptions.JoinRoomCode != null
                    ? Room.CreateRoom(CliOptions.JoinRoomCode)
                    : null;

            if (roomCode != null)
            {
                Log.Info($"CLI: 已在本地维护房间 {roomCode}");
                if (CliOptions.AddFiles.Count > 0)
                {
                    Room.AddFiles(roomCode, CliOptions.AddFiles);
                }
            }
            else if (CliOptions.AddFiles.Count > 0)
            {
                Log.Warn("CLI: 未指定 --create-room/--join-room,忽略 --add-file");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"CLI: 房间操作失败: {ex.Message}");
        }
    }

    public void Shutdown()
    {
        try { Room?.Shutdown(); } catch (Exception ex) { Log.Warn($"退出清理(房间)失败: {ex.Message}"); }
        try { Latency?.Dispose(); } catch { }
        try { Discovery?.Dispose(); } catch { }
        try { Server?.Dispose(); } catch { }
        Log.Info("========== FileTray 已退出 ==========");
    }

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];
}
