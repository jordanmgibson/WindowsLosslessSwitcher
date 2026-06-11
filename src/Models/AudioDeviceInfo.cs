namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Describes a Windows audio render device exposed to the user.
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string FriendlyName,
    bool IsDefault);
