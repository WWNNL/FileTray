using System;
using System.IO;
using System.Threading.Tasks;
using FileTray.Models;
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
        Room = new RoomService(Settings, Discovery, () => Server.Port);
        Server = new HttpApiService(Discovery, Room, () => Transfer?.SelfInfo() ?? new DeviceInfoDto());
        Transfer = new TransferService(Settings, Server, () => Room.Code);

        MainVm = new MainWindowViewModel(Settings, Discovery, Server, Room, Transfer);
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
                roomProvider: () => Room.Code);

            Log.Info($"启动完成: 别名={Settings.Alias} 指纹={Short(Settings.Fingerprint)} HTTP端口={Server.Port}");

            Avalonia.Threading.Dispatcher.UIThread.Post(() => MainVm.NotifyStarted(Server.Port));

            await RunCliActionsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"启动失败: {ex}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => MainVm.NotifyStartupFailed(ex.Message));
        }
    }

    private async Task RunCliActionsAsync()
    {
        if (CliOptions.CreateRoomCode != null)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (!Room.IsInRoom)
            {
                Room.CreateRoom(CliOptions.CreateRoomCode);
                Log.Info($"CLI: 已创建房间 {CliOptions.CreateRoomCode}");
            }
        }
        else if (CliOptions.JoinRoomCode != null)
        {
            for (var attempt = 1; attempt <= 15 && !Room.IsInRoom; attempt++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                try
                {
                    await Room.JoinRoomAsync(CliOptions.JoinRoomCode).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Info($"CLI: 加入房间尝试 {attempt} 失败: {ex.Message}");
                }
            }
        }

        if (CliOptions.AddFiles.Count > 0)
        {
            for (var i = 0; i < 40 && !Room.IsInRoom; i++)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }

            if (Room.IsInRoom)
            {
                try
                {
                    await Room.AddFilesAsync(CliOptions.AddFiles).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error($"CLI: 添加文件失败: {ex.Message}");
                }
            }
            else
            {
                Log.Warn("CLI: 未加入房间,跳过添加文件");
            }
        }
    }

    public void Shutdown()
    {
        try { Room?.Shutdown(); } catch (Exception ex) { Log.Warn($"退出清理(房间)失败: {ex.Message}"); }
        try { Discovery?.Dispose(); } catch { }
        try { Server?.Dispose(); } catch { }
        Log.Info("========== FileTray 已退出 ==========");
    }

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];
}
