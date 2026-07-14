using System.ComponentModel;
using System.Windows;
using WindowsLosslessSwitcher.ViewModels;

namespace WindowsLosslessSwitcher;

public partial class MainWindow : Window
{
    private const double PreferredMinHeight = 640;
    private readonly MainWindowViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        UpdateHeightBounds();
        DataContext = _viewModel;
        Closing += OnClosing;
        IsVisibleChanged += (_, _) => UpdateHeightBounds();
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

    private void UpdateHeightBounds()
    {
        var availableHeight = Math.Max(320, SystemParameters.WorkArea.Height - 32);
        MinHeight = Math.Min(PreferredMinHeight, availableHeight);
        MaxHeight = availableHeight;
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
