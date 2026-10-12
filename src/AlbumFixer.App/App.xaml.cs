using System.Windows;
using AlbumFixer.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WpfFoundation.Dialogs;
using WpfFoundation.Notifications;
using WpfFoundation.Operations;
using WpfFoundation.Taskbar;
using WpfFoundation.Theming;

namespace AlbumFixer.App;

public partial class App : Application
{
    private readonly ServiceProvider _services;

    public App()
    {
        var services = new ServiceCollection();

        services.AddAppLogging();
        services.AddSingleton<AlbumScanner>();
        services.AddSingleton<PreflightService>();
        services.AddSingleton<HostStagingService>();
        services.AddSingleton<LocalFlacProcessor>();
        services.AddSingleton<ExternalMetadataService>();
        services.AddSingleton<LocalMetadataEnrichmentService>();
        services.AddSingleton<LocalTrackRepairProcessor>();
        services.AddSingleton<LocalDsdProcessor>();
        services.AddSingleton<HostCommitService>();
        services.AddSingleton<StartupPrerequisiteService>();

        services.AddSingleton<Application>(this);
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ThemePreferenceStore>();
        services.AddSingleton<AppearanceViewModel>();
        services.AddSingleton(_ => new WindowsNotificationService(this));
        services.AddSingleton<INotificationService, LoggedNotifications>();
        services.AddSingleton<ITaskbarService>(_ => new TaskbarService(this));
        services.AddSingleton<IOperationFeedback>(provider => new OperationFeedback(
            provider.GetRequiredService<ITaskbarService>(), provider.GetRequiredService<INotificationService>()));
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

        var logger = _services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Starting Album Fixer {ApplicationVersion} on {OSVersion}", ApplicationVersion.Current, Environment.OSVersion);
        DispatcherUnhandledException += (_, args) => logger.LogError(args.Exception, "Unhandled exception on the UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception; Album Fixer is terminating");
        TaskScheduler.UnobservedTaskException += (_, args) => logger.LogError(args.Exception, "Unobserved task exception");
        RegisterNotifications(logger);

        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
        await _services.GetRequiredService<MainViewModel>().InitializeAsync();
    }

    // At every start and before the main window exists, so Windows shows Album Fixer's name and icon on its
    // notifications, routes a click back to this window, and groups the window with the Start menu shortcut it creates.
    private void RegisterNotifications(ILogger logger)
    {
        try
        {
            _services.GetRequiredService<INotificationService>().Register(
                "gbolotin.AlbumFixer", "Album Fixer", Path.Combine(AppContext.BaseDirectory, "Assets", "AlbumFixer.ico"));
        }
        catch (Exception error)
        {
            // The app works without notifications; the taskbar still reports every run.
            logger.LogWarning(error, "Could not register for Windows notifications");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services.GetRequiredService<ILogger<App>>().LogInformation("Album Fixer exited with code {ExitCode}", e.ApplicationExitCode);
        _services.Dispose();
        base.OnExit(e);
    }
}
