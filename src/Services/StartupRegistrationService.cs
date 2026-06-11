using Microsoft.Win32;

namespace WindowsLosslessSwitcher.Services;

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsLosslessSwitcher";

    public void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{executablePath}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
