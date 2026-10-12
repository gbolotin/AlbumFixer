using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using WpfFoundation.Dialogs;

namespace AlbumFixer.App;

public sealed record StartConfirmation(
    bool DeleteOriginals,
    bool IsBatch,
    bool IsSingleSacd,
    bool IsSingleTrackRepair,
    bool DeletesSourceAfterSuccess,
    int PreviousOutputFileCount);

/// <summary>Album Fixer's confirmations, errors, clipboard and Explorer access, so the ViewModel stays testable.</summary>
public interface IUserInteractionService
{
    Task<bool> ConfirmStartAsync(StartConfirmation confirmation);
    Task<bool> ConfirmCloseWhileRunningAsync();
    void CopyToClipboard(string text);
    Task OpenFolderAsync(string path);
    Task ShowErrorAsync(string title, string message);
}

/// <summary>
/// Shows Album Fixer's wording through WpfFoundation's shared dialog window. Every confirmation that can delete
/// a source or interrupt a job keeps the safe choice as the default button.
/// </summary>
public sealed class WpfUserInteractionService(IDialogService dialogs) : IUserInteractionService
{
    public Task<bool> ConfirmStartAsync(StartConfirmation confirmation)
    {
        if (!confirmation.DeleteOriginals && !confirmation.IsSingleTrackRepair ||
            confirmation.IsBatch ||
            !confirmation.IsSingleSacd && !confirmation.IsSingleTrackRepair && confirmation.DeletesSourceAfterSuccess)
        {
            return Task.FromResult(true);
        }

        var cleanup = confirmation.PreviousOutputFileCount > 0
            ? $"\n\n{confirmation.PreviousOutputFileCount} report-proven track file(s) from an incomplete earlier run will be deleted before staging; the prior report will be archived."
            : "";

        var message = confirmation.IsSingleTrackRepair
            ? confirmation.DeletesSourceAfterSuccess
                ? """
                   Album Fixer will copy the existing DSF or DFF tracks into local staging, prioritize their current tags and embedded artwork, use exact external-album matches and filenames only for missing fields, then verify that every native DSD audio payload is bit-for-bit unchanged. The repaired tracks will replace the originals through a rollback-capable destination transaction. Only after every final track, tag, and embedded cover passes verification will it permanently delete the single retained SACD ISO. The repaired DSF or DFF tracks are never deletion targets.

                   Start this verified repair and retained-ISO deletion?
                   """
                : """
                   Album Fixer will copy the existing FLAC, DSF, or DFF tracks into local staging, prioritize their current tags and embedded artwork, use exact external-album matches and filenames only for missing fields, then verify that every compressed or native audio payload is bit-for-bit unchanged. The repaired tracks will replace the originals through a rollback-capable destination transaction. A coexisting SACD ISO is retained when Delete originals is off; source deletion otherwise does not apply.

                   Start this existing-track repair?
                   """
            : confirmation.IsSingleSacd
            ? $"""
               Album Fixer will copy and size-check the SACD ISO in local staging, extract every reported area to DSF, repeat each extraction independently, compare extraction sizes, verify native DSD structure and tags, then recheck the committed network paths. Cryptographic hashes are not calculated. Only after every gate passes will it permanently delete the exact inventoried ISO.
               {cleanup}

               Start this SACD extraction?
               """
            : confirmation.DeletesSourceAfterSuccess
                ? $"""
                   Album Fixer will split the exact CUE-referenced FLAC or APE image into FLAC tracks and skip decoded PCM/MD5 and cryptographic hash comparisons. After the tracks are committed and pass quick FLAC, tag, artwork, and file-size checks, it will permanently delete that one inventoried source image. If artwork cannot be completed, usable tracks are delivered as incomplete work and the source image is retained.
                   {cleanup}

                   Start this run?
                   """
                : $"""
                   Album Fixer found multiple CUE-referenced FLAC and/or APE images. It will create CD<n> folders, run quick FLAC, tag, artwork, and file-size checks, and retain every original image after the tracks are committed.
                   {cleanup}

                   Start this run?
                   """;

        var (title, confirmLabel) = confirmation.IsSingleTrackRepair
            ? confirmation.DeletesSourceAfterSuccess
                ? ("Confirm verified track repair", "Repair and delete ISO")
                : ("Confirm verified track repair", "Start repair")
            : confirmation.IsSingleSacd
            ? ("Confirm verified SACD extraction", "Extract and delete ISO")
            : confirmation.DeletesSourceAfterSuccess
                ? ("Confirm source deletion", "Split and delete image")
                : ("Confirm multi-image split", "Start split");

        // Destructive: the safe "Don't start" stays the default button, as No was with the message box.
        return dialogs.ConfirmAsync(title, message.Trim(), confirmLabel, "Don't start", isDestructive: true);
    }

    public Task<bool> ConfirmCloseWhileRunningAsync() =>
        dialogs.ConfirmAsync(
            "Album Fixer is running",
            "A job is active. Cancel it and close? Staging may need review.",
            "Cancel job and close",
            "Keep running",
            isDestructive: true);

    public void CopyToClipboard(string text) => Clipboard.SetText(text);

    public Task ShowErrorAsync(string title, string message) => dialogs.ShowMessageAsync(title, message);

    public async Task OpenFolderAsync(string path)
    {
        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException(path);

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await dialogs.ShowMessageAsync("Could not open album folder", error.Message);
        }
    }
}

public interface IUiTimer
{
    event EventHandler? Tick;
    void Start();
    void Stop();
}

public sealed class DispatcherUiTimer : IUiTimer
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public event EventHandler? Tick
    {
        add => _timer.Tick += value;
        remove => _timer.Tick -= value;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
