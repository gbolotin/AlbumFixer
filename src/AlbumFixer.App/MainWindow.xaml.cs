using System.ComponentModel;
using System.Windows;

namespace AlbumFixer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeConfirmed;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
    }

    // The confirmation is asynchronous, so the first close is cancelled and the window closes again once confirmed.
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.CloseNeedsConfirmation) return;

        e.Cancel = true;
        Dispatcher.InvokeAsync(async () =>
        {
            if (!await _viewModel.ConfirmCloseAsync()) return;
            _closeConfirmed = true;
            Close();
        });
    }
}
