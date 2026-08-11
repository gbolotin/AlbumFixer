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

        services.AddSingleton<AlbumScanner>();
        services.AddSingleton<PreflightService>();
        services.AddSingleton<HostStagingService>();
        services.AddSingleton<LocalFlacProcessor>();
        services.AddSingleton<ExternalMetadataService>();
        services.AddSingleton<LocalMetadataEnrichmentService>();
        services.AddSingleton<LocalDsdProcessor>();
        services.AddSingleton<HostCommitService>();
        services.AddSingleton<StartupPrerequisiteService>();

        services.AddSingleton<IUserInteractionService, WpfUserInteractionService>();
        services.AddSingleton<IUiTimer, DispatcherUiTimer>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
        await _services.GetRequiredService<MainViewModel>().InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services.Dispose();
        base.OnExit(e);
    }
}
