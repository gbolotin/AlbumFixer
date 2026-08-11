using System.Diagnostics;
using AlbumFixer.Core;

namespace AlbumFixer.App;

public sealed record StartupPrerequisiteResult(
    IReadOnlyDictionary<string, string?> Tools,
    IReadOnlyList<string> Failures)
{
    public bool Succeeded => Failures.Count == 0;
}

public sealed class StartupPrerequisiteService
{
    private readonly PreflightService preflightService;

    public StartupPrerequisiteService(PreflightService preflightService)
    {
        this.preflightService = preflightService;
    }

    public async Task<StartupPrerequisiteResult> EnsureInstalledAsync(
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        progress?.Report("Checking ffmpeg, ffprobe, and sacd_extract…");
        var tools = await preflightService.FindToolsAsync(token);
        string? ffmpegInstallError = null;

        if (tools["ffmpeg"] is null || tools["ffprobe"] is null)
        {
            progress?.Report("FFmpeg components are missing. Installing Gyan.FFmpeg with WinGet…");
            ffmpegInstallError = await InstallFfmpegAsync(token);
        }

        progress?.Report("Verifying installed components…");
        tools = await preflightService.FindToolsAsync(token);
        var failures = new List<string>();

        if (tools["ffmpeg"] is null || tools["ffprobe"] is null)
        {
            failures.Add($"FFmpeg (ffmpeg and ffprobe): {ffmpegInstallError ?? "installation completed, but the executables were not found"}");
        }

        if (tools["sacd_extract"] is null)
        {
            failures.Add("sacd_extract: the bundled executable is missing. Reinstall Album Fixer from the complete published folder");
        }

        return new(tools, failures);
    }

    private static async Task<string?> InstallFfmpegAsync(CancellationToken token)
    {
        var winget = FindWinget();
        if (winget is null)
        {
            return "WinGet is unavailable. Install Microsoft App Installer, then restart Album Fixer";
        }

        var info = new ProcessStartInfo(winget)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "install", "--id", "Gyan.FFmpeg", "--exact", "--source", "winget", "--force",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        })
        {
            info.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var process = new Process { StartInfo = info };
            if (!process.Start()) return "WinGet could not be started";
            using var registration = timeout.Token.Register(() => Kill(process));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            return process.ExitCode == 0 ? null : UsefulError(error, output, $"WinGet exited with code {process.ExitCode}");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "WinGet installation timed out after 10 minutes";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return error.Message;
        }
    }

    private static string? FindWinget()
    {
        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(windowsApps)) return windowsApps;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "winget.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string UsefulError(string error, string output, string fallback)
    {
        var detail = string.IsNullOrWhiteSpace(error) ? output : error;
        if (string.IsNullOrWhiteSpace(detail)) return fallback;
        detail = detail.ReplaceLineEndings(" ").Trim();
        return detail.Length <= 500 ? detail : detail[..500] + "…";
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

}
