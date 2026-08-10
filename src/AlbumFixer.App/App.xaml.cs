using System.Windows;
using AlbumFixer.Core;
using Microsoft.Extensions.DependencyInjection;

namespace AlbumFixer.App;

public partial class App : Application
{
    private readonly ServiceProvider _services;

    public App()
    {
        var services = new ServiceCollection();

        services.AddSingleton(new AlbumFixerOptions(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "skills",
                "album-fixer",
                "SKILL.md")));

        services.AddSingleton<AlbumScanner>();
        services.AddSingleton<PreflightService>();
        services.AddSingleton<HostStagingService>();
        services.AddSingleton<LocalFlacProcessor>();
        services.AddSingleton<ExternalMetadataService>();
        services.AddSingleton<LocalDsdProcessor>();
        services.AddSingleton<HostCommitService>();

        services.AddSingleton<IUserInteractionService, WpfUserInteractionService>();
        services.AddSingleton<IUiTimer, DispatcherUiTimer>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services.Dispose();
        base.OnExit(e);
    }
}
