using System.ComponentModel;
using System.Windows;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Controls;
using WindowsLosslessSwitcher.ViewModels;

namespace WindowsLosslessSwitcher.Views;

/// <summary>
/// The 1a now-playing hero card — the app's main window. Tray-first: closing hides instead of
/// exiting, exactly like the previous main window.
/// </summary>
public partial class HeroWindow : NocturneWindow
{
    private bool _allowClose;

    public HeroWindow(MainWindowViewModel viewModel, ISpectrumSource spectrumSource)
    {
        InitializeComponent();
        DataContext = viewModel;
        TitleBarSpectrograph.Attach(spectrumSource);
        Closing += OnClosing;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 32);
    }

    public event Action? WindowHidden;

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
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
}
