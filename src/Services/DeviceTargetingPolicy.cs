using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public static class DeviceTargetingPolicy
{
    public static AudioDeviceInfo? SelectTargetDevice(
        AppSettings settings,
        IReadOnlyList<AudioDeviceInfo> devices,
        AudioDeviceInfo? defaultDevice)
    {
        return settings.DeviceSelectionMode switch
        {
            DeviceSelectionMode.FollowDefault => defaultDevice,
            DeviceSelectionMode.PinnedDevice => devices.FirstOrDefault(device => device.Id == settings.PinnedDeviceId),
            _ => defaultDevice,
        };
    }
}
