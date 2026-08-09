using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using AlbumFixer.Core;
using Microsoft.Win32;

namespace AlbumFixer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.ConfirmStart = ConfirmStart;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Add an album or parent folder containing albums", Multiselect = false };
        if (_viewModel.BrowseInitialDirectory is { } initialDirectory) picker.InitialDirectory = initialDirectory;
        if (picker.ShowDialog(this) == true) await _viewModel.AddSourceFoldersAsync([picker.FolderName]);
    }

    private void SourceFolders_DragOver(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = !_viewModel.Busy && paths?.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void SourceFolders_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await _viewModel.AddSourceFoldersAsync(paths);
        e.Handled = true;
    }

    private async void PreflightAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string albumFolderPath }) return;
        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(albumFolderPath)) throw new DirectoryNotFoundException(albumFolderPath);
                Process.Start(new ProcessStartInfo(albumFolderPath) { UseShellExecute = true });
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, error.Message, "Could not open album folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ConfirmStart()
    {
        var cleanup = _viewModel.PreviousOutputFileCount > 0
            ? $"\n\n{_viewModel.PreviousOutputFileCount} report-proven track file(s) from an incomplete earlier run will be deleted before staging; the prior report will be archived."
            : "";
        var message = _viewModel.IsBatch ? $"""
Album Fixer will move {_viewModel.RunnableAlbumCount} admitted albums through a hardware-aware bounded pipeline: {_viewModel.BatchPipelineDescription}. {_viewModel.AlbumCount - _viewModel.RunnableAlbumCount} blocked album(s) will be skipped. Every admitted album uses unique local and destination staging. SACD areas are extracted sequentially and verified independently; failed, canceled, blocked, or artwork-incomplete albums retain their originals.{cleanup}

Start this {_viewModel.AlbumCount}-album batch?
""" : _viewModel.IsSingleSacd ? $"""
Album Fixer will copy and SHA-256 verify the SACD ISO in local staging, extract every reported area to DSF, repeat each extraction independently, verify native DSD signal properties and unchanged audio payloads through tagging, then reverify the committed network paths. Only after every gate passes will it permanently delete the exact inventoried ISO.
{cleanup}

Start this SACD extraction?
""" : _viewModel.DeletesSourceAfterSuccess ? $"""
Album Fixer will skip decoded PCM/MD5 comparison. After the tracks are committed and pass quick FLAC, tag, artwork, and copy-hash checks, it will permanently delete the exact inventoried FLAC image. If artwork cannot be completed, usable tracks are delivered as incomplete work and the source image is retained.
{cleanup}

Start this run?
""" : $"""
Album Fixer found multiple FLAC images. It will create CD<n> folders, run quick FLAC, tag, artwork, and copy-hash checks, and retain every original image after the tracks are committed.
{cleanup}

Start this run?
""";
        var title = _viewModel.IsBatch ? "Confirm parallel album batch" : _viewModel.IsSingleSacd ? "Confirm verified SACD extraction" : _viewModel.DeletesSourceAfterSuccess ? "Confirm source deletion" : "Confirm multi-image split";
        return MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.Busy && MessageBox.Show(this, "A job is active. Cancel it and close? Staging may need review.", "Album Fixer is running", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        if (_viewModel.Busy) _viewModel.CancelCommand.Execute(null);
        _viewModel.Dispose();
    }
}
