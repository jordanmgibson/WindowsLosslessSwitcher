using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Asks DWM to round a borderless window's corners on Windows 11 (build 22000+). Windows 10
/// has no per-window corner API, so the Nocturne windows ship square corners there — same
/// layout, same ring, OS-native corner behavior.
/// </summary>
internal static class WindowCornerInterop
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void TryApplyRoundedCorners(Window window)
    {
        if (Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var preference = DwmwcpRound;
        // Best-effort: a failure just leaves square corners.
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }
}
