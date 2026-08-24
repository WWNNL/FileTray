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
            desktop.MainWindow = new MainWindow { DataContext = services.MainVm };
            desktop.Exit += (_, _) => services.Shutdown();
            _ = Task.Run(() => services.StartupAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
