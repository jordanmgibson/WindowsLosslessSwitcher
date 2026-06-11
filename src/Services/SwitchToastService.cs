using System.Windows;
using WindowsLosslessSwitcher.Models;
using Application = System.Windows.Application;

namespace WindowsLosslessSwitcher.Services;

public sealed class SwitchToastService : IDisposable
{
    private SwitchToastWindow? _currentToast;

    public void ShowSwitchedFormat(string? deviceName, AudioFormatCandidate format, TrackSnapshot? track)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            ShowSwitchedFormatCore(deviceName, format, track);
            return;
        }

        Application.Current.Dispatcher.Invoke(() => ShowSwitchedFormatCore(deviceName, format, track));
    }

    public void Dispose()
    {
        if (_currentToast is null)
        {
            return;
        }

        _currentToast.Close();
        _currentToast = null;
    }

    private void ShowSwitchedFormatCore(string? deviceName, AudioFormatCandidate format, TrackSnapshot? track)
    {
        _currentToast?.Close();

        var toast = new SwitchToastWindow(
            "Switched audio format",
            AudioFormatTextFormatter.Format(format),
            deviceName,
            BuildTrackDetails(track));
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_currentToast, toast))
            {
                _currentToast = null;
            }
        };

        _currentToast = toast;
        toast.Show();
        toast.StartAutoClose();
    }

    private static string? BuildTrackDetails(TrackSnapshot? track)
    {
        if (track is null)
        {
            return null;
        }

        return string.Join(
            Environment.NewLine,
            $"Song: {track.Title ?? "Unknown Title"}",
            $"Artist: {track.Artist ?? "Unknown Artist"}",
            $"Album: {track.Album ?? "Unknown Album"}");
    }
}
