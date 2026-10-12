using System.Diagnostics;
using System.IO;
using AlbumFixer.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace AlbumFixer.App;

/// <summary>
/// Album Fixer's log, as UPSWarden and AudioAwake log: Serilog behind <c>ILogger&lt;T&gt;</c>, writing rolling files in
/// Serilog's compact JSON format (one event per line, readable in Seq or Compact Log Viewer) under
/// <see cref="LogsFolder"/>, with the application's name and version on every event.
/// </summary>
public static class AppLogging
{
    /// <summary><c>%LOCALAPPDATA%\Album Fixer\Logs</c>.</summary>
    public static string LogsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Album Fixer",
        "Logs");

    /// <summary>
    /// Registers <c>ILogger&lt;T&gt;</c> backed by Serilog. Disposing the service provider flushes the log.
    /// Logging never throws into Album Fixer: Serilog swallows sink failures.
    /// </summary>
    public static IServiceCollection AddAppLogging(this IServiceCollection services)
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Album Fixer")
            .Enrich.WithProperty("ApplicationVersion", ApplicationVersion.Current)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(LogsFolder, "AlbumFixer-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
        return services.AddLogging(builder => builder.AddSerilog(logger, dispose: true));
    }

    /// <summary>Opens <see cref="LogsFolder"/> in File Explorer, creating it when it does not exist yet.</summary>
    public static void OpenLogsFolder()
    {
        Directory.CreateDirectory(LogsFolder);
        Process.Start(new ProcessStartInfo { FileName = LogsFolder, UseShellExecute = true });
    }
}
