using WindowsLosslessSwitcher.Models;
using Application = System.Windows.Application;

namespace WindowsLosslessSwitcher.Services;

public sealed class SwitchToastService : IDisposable
{
    private readonly Queue<FormatCacheToastRequest> _pendingFormatCacheUpdates = new();
    private readonly Func<string, string, string?, string?, ISwitchToastWindow> _createToast;
    private readonly Func<bool> _checkAccess;
    private readonly Action<Action> _invoke;
    private ISwitchToastWindow? _currentToast;
    private ToastKind? _currentToastKind;
    private bool _disposed;

    public SwitchToastService()
        : this(
            (title, message, deviceName, trackDetails) =>
                new SwitchToastWindow(title, message, deviceName, trackDetails),
            () => Application.Current.Dispatcher.CheckAccess(),
            action => Application.Current.Dispatcher.Invoke(action))
    {
    }

    internal SwitchToastService(
        Func<string, string, string?, string?, ISwitchToastWindow> createToast,
        Func<bool> checkAccess,
        Action<Action> invoke)
    {
        _createToast = createToast;
        _checkAccess = checkAccess;
        _invoke = invoke;
    }

    public void ShowFormatCacheUpdated(
        string? deviceName,
        AudioFormatCandidate previousFormat,
        AudioFormatCandidate updatedFormat,
        TrackSnapshot? track)
    {
        var request = new FormatCacheToastRequest(deviceName, previousFormat, updatedFormat, track);
        if (_checkAccess())
        {
            ShowFormatCacheUpdatedCore(request);
            return;
        }

        _invoke(() => ShowFormatCacheUpdatedCore(request));
    }

    public void ShowSwitchedFormat(string? deviceName, AudioFormatCandidate format, TrackSnapshot? track)
    {
        if (_checkAccess())
        {
            ShowSwitchedFormatCore(deviceName, format, track);
            return;
        }

        _invoke(() => ShowSwitchedFormatCore(deviceName, format, track));
    }

    public void DiscardPendingFormatCacheUpdates()
    {
        if (_checkAccess())
        {
            DiscardPendingFormatCacheUpdatesCore();
            return;
        }

        _invoke(DiscardPendingFormatCacheUpdatesCore);
    }

    public void Dispose()
    {
        if (!_checkAccess())
        {
            _invoke(DisposeCore);
            return;
        }

        DisposeCore();
    }

    private void ShowFormatCacheUpdatedCore(FormatCacheToastRequest request)
    {
        if (_disposed)
        {
            return;
        }

        if (_currentToast is not null)
        {
            _pendingFormatCacheUpdates.Enqueue(request);
            return;
        }

        ShowFormatCacheToast(request);
    }

    private void ShowSwitchedFormatCore(string? deviceName, AudioFormatCandidate format, TrackSnapshot? track)
    {
        if (_disposed)
        {
            return;
        }

        var toast = CreateToast(
            ToastKind.Switch,
            "Switched audio format",
            AudioFormatTextFormatter.Format(format),
            deviceName,
            track);

        // WPF raises Closed synchronously. Make the replacement current first so the old
        // window's handler cannot drain queued cache notifications between switch toasts.
        var previousToast = _currentToast;
        _currentToast = toast;
        _currentToastKind = ToastKind.Switch;
        previousToast?.Close();
        toast.Show();
        toast.StartAutoClose();
    }

    private void ShowFormatCacheToast(FormatCacheToastRequest request)
    {
        var toast = CreateToast(
            ToastKind.FormatCacheUpdate,
            "Format updated for next playback",
            $"{AudioFormatTextFormatter.Format(request.PreviousFormat)} -> {AudioFormatTextFormatter.Format(request.UpdatedFormat)}",
            request.DeviceName,
            request.Track);
        _currentToast = toast;
        _currentToastKind = ToastKind.FormatCacheUpdate;
        toast.Show();
        toast.StartAutoClose();
    }

    private ISwitchToastWindow CreateToast(
        ToastKind kind,
        string title,
        string message,
        string? deviceName,
        TrackSnapshot? track)
    {
        var toast = _createToast(title, message, deviceName, BuildTrackDetails(track));
        toast.Closed += (_, _) => OnToastClosed(toast, kind);
        return toast;
    }

    private void OnToastClosed(ISwitchToastWindow toast, ToastKind kind)
    {
        if (!ReferenceEquals(_currentToast, toast) || _currentToastKind != kind)
        {
            return;
        }

        _currentToast = null;
        _currentToastKind = null;
        ShowNextPendingFormatCacheUpdate();
    }

    private void ShowNextPendingFormatCacheUpdate()
    {
        if (_disposed || _currentToast is not null || _pendingFormatCacheUpdates.Count == 0)
        {
            return;
        }

        ShowFormatCacheToast(_pendingFormatCacheUpdates.Dequeue());
    }

    private void DiscardPendingFormatCacheUpdatesCore()
    {
        _pendingFormatCacheUpdates.Clear();
        if (_currentToastKind != ToastKind.FormatCacheUpdate)
        {
            return;
        }

        var toast = _currentToast;
        _currentToast = null;
        _currentToastKind = null;
        toast?.Close();
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingFormatCacheUpdates.Clear();
        var toast = _currentToast;
        _currentToast = null;
        _currentToastKind = null;
        toast?.Close();
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

    private enum ToastKind
    {
        Switch,
        FormatCacheUpdate,
    }

    private sealed record FormatCacheToastRequest(
        string? DeviceName,
        AudioFormatCandidate PreviousFormat,
        AudioFormatCandidate UpdatedFormat,
        TrackSnapshot? Track);
}

internal interface ISwitchToastWindow
{
    event EventHandler? Closed;

    void Show();

    void Close();

    void StartAutoClose();
}
