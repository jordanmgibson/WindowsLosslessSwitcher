using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Controls;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.ViewModels;
using WindowsLosslessSwitcher.Views;

internal static class DesignVerification
{
    internal static void Run(Application application, MainWindowViewModel model)
    {
        var output = Path.GetFullPath("artifacts/ui-verification");
        Directory.CreateDirectory(output);
        model.ReplaceDevices([new AudioDeviceInfo("test-dac", "Test DAC", true)]);
        model.UpdateActiveTargetCapabilities(new CurrentTargetDeviceCapabilitiesSnapshot(
            "Test DAC", [new AudioFormatCandidate(44100, 16, 2), new AudioFormatCandidate(96000, 24, 2)]));
        model.UpdateAppVersion(new UpdateStatusSnapshot("0.1.0", "Up to date.",
            UpdateActionKind.None, null, false, false, true, false, true));
        var spectrum = new SilentSpectrum();
        var settings = new SettingsWindow(model, spectrum);
        var root = (FrameworkElement)settings.Content;
        var navigation = (ListBox)settings.FindName("NavList");
        var names = new[] { "status", "switching", "notifications", "cache", "updates" };
        foreach (var width in new[] { 760d, 920d })
        {
            for (var index = 0; index < names.Length; index++)
            {
                navigation.SelectedIndex = index;
                Layout(root, width, 720);
                var page = (FrameworkElement)settings.FindName(new[] { "StatusPage", "SwitchingPage", "NotificationsPage", "CachePage", "UpdatesPage" }[index]);
                CheckHorizontalBounds(page);
                Render(root, Path.Combine(output, $"{names[index]}-{width}.png"), 1);
            }
        }
        navigation.SelectedIndex = 1;
        Layout(root, 760, 520);
        var switching = (FrameworkElement)settings.FindName("SwitchingPage");
        var rate = Descendants(switching).OfType<ComboBox>().First(c => AutomationProperties.GetName(c) == "Native sample rate");
        if (rate.ActualWidth < 150 || rate.ActualHeight < 32) throw new Exception("Native format field is too small.");
        foreach (var control in Descendants(switching).OfType<ToggleButton>().Where(c => c.IsKeyboardFocusWithin || c.ActualWidth > 0))
        {
            if (control.Focusable && string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(control)?.GetName()))
                throw new Exception("A settings toggle lacks an accessible name.");
        }
        Render(root, Path.Combine(output, "switching-200dpi.png"), 2);
        var scroller = Descendants(root).OfType<ScrollViewer>().First(s => s.Content is Grid grid && grid.Children.Contains(switching));
        scroller.ScrollToVerticalOffset(420);
        Layout(root, 760, 520);
        Render(root, Path.Combine(output, "native-format-controls.png"), 1);
        scroller.ScrollToHome();
        Layout(root, 760, 520);
        AccessibleTheme.Apply(application.Resources, true);
        Layout(root, 760, 520);
        var foreground = (SolidColorBrush)application.FindResource("Nocturne.Brush.Text");
        var background = (SolidColorBrush)application.FindResource("Nocturne.Brush.Ground");
        if (Contrast(foreground.Color, background.Color) < 4.5) throw new Exception("Contrast palette has unreadable text.");
        Render(root, Path.Combine(output, "switching-contrast.png"), 1);
        AccessibleTheme.Apply(application.Resources, false);
        Layout(root, 760, 520);
        foreground = (SolidColorBrush)application.FindResource("Nocturne.Brush.Text40");
        background = (SolidColorBrush)application.FindResource("Nocturne.Brush.Surface");
        if (foreground.Color.A != 255 || Contrast(foreground.Color, background.Color) < 4.5)
            throw new Exception("Secondary settings text lacks sufficient contrast.");
        AccessibleTheme.Apply(application.Resources, false, true);
        Layout(root, 760, 520);
        foreground = (SolidColorBrush)application.FindResource("Nocturne.Brush.Text40");
        background = (SolidColorBrush)application.FindResource("Nocturne.Brush.Surface");
        if (Contrast(foreground.Color, background.Color) < 4.5) throw new Exception("Light appearance has unreadable secondary text.");
        Render(root, Path.Combine(output, "switching-light.png"), 1);
        AccessibleTheme.Apply(application.Resources, false);
        var hero = new HeroWindow(model, spectrum);
        var heroRoot = (FrameworkElement)hero.Content;
        Layout(heroRoot, 440, 680);
        Render(heroRoot, Path.Combine(output, "main.png"), 1);
        Layout(heroRoot, 440, 500);
        Render(heroRoot, Path.Combine(output, "main-compact-200dpi.png"), 2);
        hero.AllowCloseAndClose();
        settings.AllowCloseAndClose();
        Console.WriteLine("PASS: Five settings pages at 760/920 DIP, compact main window, 200% raster output, light/dark/contrast palettes, control bounds and accessibility names.");
        Console.WriteLine("UI previews: " + output);
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        // Force deferred style lookup; keyboard focus otherwise reveals invalid forward resources only at runtime.
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
            _ = element.FocusVisualStyle;
    }

    private static void CheckHorizontalBounds(FrameworkElement page)
    {
        foreach (var element in Descendants(page).OfType<Control>().Where(c => c is Button or ComboBox or ToggleButton))
        {
            if (element.ActualWidth <= 0 || !VisibleThrough(element, page)) continue;
            var bounds = element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
            if (bounds.Left < -1 || bounds.Right > page.ActualWidth + 1)
                throw new Exception($"Control exceeds page width: {element.GetType().Name} / {element.Name}: {bounds}, page width {page.ActualWidth}.");
        }
    }

    private static bool VisibleThrough(DependencyObject node, DependencyObject root)
    {
        for (var current = node; current is not null && current != root; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Render(FrameworkElement root, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte component) { var value = component / 255d; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private sealed class SilentSpectrum : ISpectrumSource
    {
        public float[] CurrentBars { get; } = new float[16];
        public bool IsActive => false;
        public IDisposable AcquireVisibleLease() => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
