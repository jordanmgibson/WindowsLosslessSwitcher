using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Controls;
using WindowsLosslessSwitcher.ViewModels;

namespace WindowsLosslessSwitcher.Views;

/// <summary>
/// The 1b dashboard: nav rail with five pages over the shared view model. Created lazily and
/// hidden (not closed) so the selected page survives reopening.
/// </summary>
public partial class SettingsWindow : NocturneWindow
{
    private bool _allowClose;

    public SettingsWindow(MainWindowViewModel viewModel, ISpectrumSource spectrumSource)
    {
        InitializeComponent();
        EnableResizeBorder(6);
        DataContext = viewModel;
        NavTileSpectrograph.Attach(spectrumSource);
        Closing += OnClosing;
        NavList.SelectedIndex = 0;
    }

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
    }

    private void NavList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UIElement[] pageElements = [StatusPage, SwitchingPage, NotificationsPage, CachePage, UpdatesPage];
        for (var i = 0; i < pageElements.Length; i++)
        {
            pageElements[i].Visibility = i == NavList.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
