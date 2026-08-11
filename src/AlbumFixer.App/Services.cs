using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AlbumFixer.App;

public sealed record StartConfirmation(
    bool DeleteOriginals,
    bool IsBatch,
    bool IsSingleSacd,
    bool DeletesSourceAfterSuccess,
    int PreviousOutputFileCount);

public interface IUserInteractionService
{
    string? SelectSourceFolder(string? initialDirectory);
    bool ConfirmStart(StartConfirmation confirmation);
    bool ConfirmCloseWhileRunning();
    void CopyToClipboard(string text);
    Task OpenFolderAsync(string path);
    void ShowError(string title, string message);
}

public sealed class WpfUserInteractionService : IUserInteractionService
{
    private static Window? Owner => Application.Current.MainWindow;

    public string? SelectSourceFolder(string? initialDirectory)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Add an album or parent folder containing albums",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory))
            picker.InitialDirectory = initialDirectory;

        return picker.ShowDialog(Owner) == true ? picker.FolderName : null;
    }

    public bool ConfirmStart(StartConfirmation confirmation)
    {
        if (!confirmation.DeleteOriginals ||
            confirmation.IsBatch ||
            !confirmation.IsSingleSacd && confirmation.DeletesSourceAfterSuccess)
        {
            return true;
        }

        var cleanup = confirmation.PreviousOutputFileCount > 0
            ? $"\n\n{confirmation.PreviousOutputFileCount} report-proven track file(s) from an incomplete earlier run will be deleted before staging; the prior report will be archived."
            : "";

        var message = confirmation.IsSingleSacd
            ? $"""
               Album Fixer will copy and size-check the SACD ISO in local staging, extract every reported area to DSF, repeat each extraction independently, compare extraction sizes, verify native DSD structure and tags, then recheck the committed network paths. Cryptographic hashes are not calculated. Only after every gate passes will it permanently delete the exact inventoried ISO.
               {cleanup}

               Start this SACD extraction?
               """
            : confirmation.DeletesSourceAfterSuccess
                ? $"""
                   Album Fixer will skip decoded PCM/MD5 and cryptographic hash comparisons. After the tracks are committed and pass quick FLAC, tag, artwork, and file-size checks, it will permanently delete the exact inventoried FLAC image. If artwork cannot be completed, usable tracks are delivered as incomplete work and the source image is retained.
                   {cleanup}

                   Start this run?
                   """
                : $"""
                   Album Fixer found multiple FLAC images. It will create CD<n> folders, run quick FLAC, tag, artwork, and file-size checks, and retain every original image after the tracks are committed.
                   {cleanup}

                   Start this run?
                   """;

        var title = confirmation.IsSingleSacd
            ? "Confirm verified SACD extraction"
            : confirmation.DeletesSourceAfterSuccess
                ? "Confirm source deletion"
                : "Confirm multi-image split";

        return MessageBox.Show(
            Owner,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmCloseWhileRunning() =>
        MessageBox.Show(
            Owner,
            "A job is active. Cancel it and close? Staging may need review.",
            "Album Fixer is running",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void CopyToClipboard(string text) => Clipboard.SetText(text);

    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

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
            MessageBox.Show(
                Owner,
                error.Message,
                "Could not open album folder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
