using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace AlbumFixer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    public MainWindow() { InitializeComponent(); DataContext = _viewModel; _viewModel.ConfirmStart = ConfirmStart; }
    private void Browse_Click(object sender, RoutedEventArgs e) { var picker = new OpenFolderDialog { Title = "Choose one album folder", Multiselect = false }; if (Directory.Exists(_viewModel.AlbumPath)) picker.InitialDirectory = _viewModel.AlbumPath; if (picker.ShowDialog(this) == true) _viewModel.AlbumPath = picker.FolderName; }
    private bool ConfirmStart(bool delete)
    {
        var message = delete ? "After every staging, copy-back, final-path, and report gate passes, Album Fixer may permanently delete each exact inventoried source image. Failed or uncertain jobs keep their sources.\n\nStart this verified-deletion run?" : "Album Fixer will process and verify the album, but retain every original.\n\nStart this run?";
        return MessageBox.Show(this, message, "Confirm Album Fixer run", MessageBoxButton.YesNo, delete ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_viewModel.Busy && MessageBox.Show(this, "A job is active. Cancel it and close? Staging may need review.", "Album Fixer is running", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { e.Cancel = true; return; } if (_viewModel.Busy) _viewModel.CancelCommand.Execute(null); _viewModel.Dispose(); }
}
