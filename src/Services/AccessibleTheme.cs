using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using SystemColors = System.Windows.SystemColors;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace WindowsLosslessSwitcher.Services;

/// <summary>Maps dynamic theme resources to Windows contrast colors without changing OS settings.</summary>
public static class AccessibleTheme
{
    private static readonly string[] TextKeys = ["Text", "Text80", "Text75", "Text70", "Text65", "Text60", "Text55", "Text50", "Text45", "Text40", "Text35", "Text30", "Muted", "OffKnob"];
    private static readonly string[] SurfaceKeys = ["Ground", "Surface", "Recessed", "Hover"];
    private static readonly string[] AccentKeys = ["Accent", "AccentLight", "AccentPulse", "FocusGlow"];
    private static readonly string[] BorderKeys = ["Ring", "MidRing", "Hairline16", "Hairline14", "Hairline10", "Hairline08", "Hairline06"];

    public static bool UseLightTheme
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
            }
            catch { return false; }
        }
    }

    public static void Apply(ResourceDictionary resources, bool highContrast, bool lightTheme = false)
    {
        Set(TextKeys, SystemColors.WindowTextBrush);
        Set(SurfaceKeys, SystemColors.WindowBrush);
        Set(AccentKeys, SystemColors.WindowTextBrush);
        Set(BorderKeys, SystemColors.WindowTextBrush);
        Set(["AccentTint", "AccentDeep", "AccentHoverFill"], SystemColors.WindowBrush);
        if (!highContrast && lightTheme)
        {
            foreach (var key in TextKeys) Color(key, key is "Text" or "Text80" or "Text75" ? "#FF1C1C1C" : "#FF525252");
            Color("Ground", "#FFF3F3F3"); Color("Surface", "#FFFAFAFA"); Color("Recessed", "#FFFFFFFF"); Color("Hover", "#FFE8E8E8");
            Color("Accent", "#FFB91C3C"); Color("AccentLight", "#FF9F1239"); Color("AccentPulse", "#FFB91C3C"); Color("FocusGlow", "#40B91C3C");
            Color("AccentTint", "#FFFCE8EC"); Color("AccentDeep", "#FFE7B4BE"); Color("AccentHoverFill", "#1FB91C3C");
            Color("Ring", "#FF878787"); Color("MidRing", "#FF6D6D6D");
            foreach (var key in BorderKeys.Where(k => k.StartsWith("Hairline"))) Color(key, "#29000000");
        }

        void Color(string key, string value) => resources["Nocturne.Brush." + key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));

        void Set(IEnumerable<string> keys, Brush value)
        {
            foreach (var key in keys)
            {
                var name = "Nocturne.Brush." + key;
                if (highContrast) resources[name] = value;
                else resources.Remove(name);
            }
        }
    }
}
