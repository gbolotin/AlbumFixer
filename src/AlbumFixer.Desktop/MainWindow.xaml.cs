using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace AlbumFixer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    public MainWindow() { InitializeComponent(); DataContext = _viewModel; _viewModel.ConfirmStart = ConfirmStart; }
    private void Browse_Click(object sender, RoutedEventArgs e) { var picker = new OpenFolderDialog { Title = "Choose one album folder", Multiselect = false }; if (Directory.Exists(_viewModel.AlbumPath)) picker.InitialDirectory = _viewModel.AlbumPath; if (picker.ShowDialog(this) == true) _viewModel.AlbumPath = picker.FolderName; }
    private bool ConfirmStart()
    {
        const string message = """
Album Fixer will skip decoded PCM/MD5 comparison. After the tracks are committed and pass quick FLAC, tag, artwork, and copy-hash checks, it will permanently delete the exact inventoried FLAC image. A failure or cancellation before the deletion step keeps the source.

Start this run?
""";
        return MessageBox.Show(this, message, "Confirm source deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_viewModel.Busy && MessageBox.Show(this, "A job is active. Cancel it and close? Staging may need review.", "Album Fixer is running", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { e.Cancel = true; return; } if (_viewModel.Busy) _viewModel.CancelCommand.Execute(null); _viewModel.Dispose(); }
}
