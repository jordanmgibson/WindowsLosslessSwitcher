using System.ComponentModel;
using System.Windows;
using WindowsLosslessSwitcher.ViewModels;

namespace WindowsLosslessSwitcher;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
        Closing += OnClosing;
    }

    public event Action? WindowHidden;

    public event Action<string>? DiagnosticsExportRequested;

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void HideButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        WindowHidden?.Invoke();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        WindowHidden?.Invoke();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _viewModel.ExportDiagnosticsRequested += HandleExportDiagnosticsRequested;
    }

    private void HandleExportDiagnosticsRequested()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".log",
            Filter = "Log files (*.log)|*.log|All files (*.*)|*.*",
            FileName = $"windows-lossless-switcher-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };

        if (dialog.ShowDialog(this) == true)
        {
            DiagnosticsExportRequested?.Invoke(dialog.FileName);
        }
    }

}
