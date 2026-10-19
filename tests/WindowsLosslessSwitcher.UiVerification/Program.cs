using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Controls;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using WindowsLosslessSwitcher.Views.SettingsPages;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Materialize the actual release BAML without constructing the app, opening a window,
        // starting audio services, or touching the user's settings/startup registration.
        var application = new Application();
        foreach (var theme in new[] { "Colors", "Typography", "Icons", "Controls" })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WindowsLosslessSwitcher;component/Themes/Nocturne.{theme}.xaml")
            });
        }
        var model = new MainWindowViewModel();
        var page = new SwitchingPage { DataContext = model };
        page.Measure(new Size(600, 1600));
        page.Arrange(new Rect(0, 0, 600, 1600));
        page.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var toggle = FindToggle(page) ?? throw new Exception("Intro toggle binding was not found.");
        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateTarget();
        if (toggle.IsChecked != true) throw new Exception("Intro toggle did not load its enabled default.");
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
        if (model.RewindAfterFormatSwitch) throw new Exception("Intro toggle did not update the view model.");
        if (!ReleaseRepositoryOptions.IsConfigured) throw new Exception("Upstream update repository must remain configured.");
        model.UpdateStatus(new SwitchingStatus("Resolver: UserVerified", "Test DAC",
            new TrackSnapshot("AppleMusic.exe", null, "Dirty Diana", "Michael Jackson", "Number Ones", "test", DateTimeOffset.UtcNow),
            new ResolvedAudioFormat(96000, 24, ResolutionConfidence.Exact, AudioFormatSource.UserVerified, "Native player correction"),
            new AudioFormatCandidate(96000, 24, 2), null));
        page.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var rateSelector = FindRateSelector(page) ?? throw new Exception("Native sample-rate selector was not found.");
        rateSelector.GetBindingExpression(ComboBox.SelectedItemProperty)!.UpdateTarget();
        if (!Equals(rateSelector.SelectedItem, 96000)) throw new Exception("Native correction did not populate the selector.");
        rateSelector.SetCurrentValue(ComboBox.SelectedItemProperty, 44100);
        rateSelector.GetBindingExpression(ComboBox.SelectedItemProperty)!.UpdateSource();
        if (model.NativeSampleRateHz != 44100) throw new Exception("Native sample-rate selector did not update the view model.");
        if (!model.SaveNativeFormatCorrectionCommand.CanExecute(null) || model.SourceChipText != "Native correction")
            throw new Exception("Native correction command/source label is unavailable.");
        Console.WriteLine("PASS: Release WPF settings page loads, intro and native format controls bind both ways, upstream updates remain configured.");
        model.RewindAfterFormatSwitch = true;
        model.SwitchBitDepth = true;
        model.PreferClosestSampleRateMultiple = true;
        model.NativeSampleRateHz = 96000;
        DesignVerification.Run(application, model);
        application.Shutdown();
    }

    private static ToggleButton? FindToggle(DependencyObject node, string property = "RewindAfterFormatSwitch")
    {
        if (node is ToggleButton toggle &&
            BindingOperations.GetBinding(toggle, ToggleButton.IsCheckedProperty)?.Path.Path == property) return toggle;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependency && FindToggle(dependency, property) is { } found) return found;
        }
        return null;
    }

    private static ComboBox? FindRateSelector(DependencyObject node)
    {
        if (node is ComboBox selector &&
            BindingOperations.GetBinding(selector, ComboBox.SelectedItemProperty)?.Path.Path == "NativeSampleRateHz") return selector;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject dependency && FindRateSelector(dependency) is { } found) return found;
        return null;
    }
}
