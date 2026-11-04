using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public static class DeviceTargetingPolicy
{
    public static AudioDeviceInfo? GetCurrentTargetDevice(
        AppSettings settings,
        IAudioEndpointController audioEndpointController)
    {
        if (settings.DeviceSelectionMode == DeviceSelectionMode.FollowDefault)
        {
            return audioEndpointController.GetDefaultRenderDevice();
        }

        var devices = audioEndpointController.GetRenderDevices();
        var defaultDevice = devices.FirstOrDefault(device => device.IsDefault)
            ?? audioEndpointController.GetDefaultRenderDevice();
        return SelectTargetDevice(settings, devices, defaultDevice);
    }

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
