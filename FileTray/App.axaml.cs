using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FileTray.Services;
using FileTray.Views;

namespace FileTray;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Instance;
            services.Initialize();

            var mainWindow = new MainWindow { DataContext = services.MainVm };
            desktop.MainWindow = mainWindow;
            // Avalonia 没有 ShutdownMode,窗口关闭时显式退出,不留后台进程
            mainWindow.Closed += (_, _) => desktop.Shutdown();
            desktop.Exit += (_, _) => services.Shutdown();
            _ = Task.Run(() => services.StartupAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
