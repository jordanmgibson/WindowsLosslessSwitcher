using System.Text.RegularExpressions;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WindowsLosslessSwitcher.ViewModels;

namespace WindowsLosslessSwitcher.Views.SettingsPages;

public partial class CachePage : UserControl
{
    public CachePage()
    {
        InitializeComponent();
    }

    // The refresh-days chips are a dynamic single-select group over an int property; display
    // state comes from a one-way MultiBinding, selection is applied here.
    private void CacheDayChip_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: int days } && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.FormatCacheRefreshDays = days;
        }
    }

    private void StorefrontTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[a-zA-Z]+$");
    }
}
