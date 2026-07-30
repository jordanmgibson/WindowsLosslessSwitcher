using WindowsLosslessSwitcher.Models;
using Application = System.Windows.Application;

namespace WindowsLosslessSwitcher.Services;

public sealed class SwitchToastService : IDisposable
{
    // A skip-heavy session can enqueue cache updates faster than switch toasts let them drain;
    // beyond this the oldest are stale enough that showing them would just be noise.
    private const int MaxPendingFormatCacheUpdates = 3;

    private readonly List<FormatCacheToastRequest> _pendingFormatCacheUpdates = new();
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
            EnqueuePendingFormatCacheUpdate(request);
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
        ShowCurrentToast(toast);
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
        ShowCurrentToast(toast);
    }

    // A Show() that throws (window creation during shutdown, display topology change) must not
    // leave the dead window registered as current: its Closed event will never fire, and every
    // later cache-update toast would queue behind it forever.
    private void ShowCurrentToast(ISwitchToastWindow toast)
    {
        try
        {
            toast.Show();
            toast.StartAutoClose();
        }
        catch
        {
            if (ReferenceEquals(_currentToast, toast))
            {
                _currentToast = null;
                _currentToastKind = null;
            }

            throw;
        }
    }

    private void EnqueuePendingFormatCacheUpdate(FormatCacheToastRequest request)
    {
        // Latest update wins per track: a queued stale entry for the same track would show an
        // outdated transition once the queue drains.
        var key = request.Track?.UniqueKey;
        if (key is not null)
        {
            _pendingFormatCacheUpdates.RemoveAll(pending =>
                string.Equals(pending.Track?.UniqueKey, key, StringComparison.Ordinal));
        }

        _pendingFormatCacheUpdates.Add(request);
        while (_pendingFormatCacheUpdates.Count > MaxPendingFormatCacheUpdates)
        {
            _pendingFormatCacheUpdates.RemoveAt(0);
        }
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

        var next = _pendingFormatCacheUpdates[0];
        _pendingFormatCacheUpdates.RemoveAt(0);
        ShowFormatCacheToast(next);
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
