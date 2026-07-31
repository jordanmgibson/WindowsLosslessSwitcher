using WindowsLosslessSwitcher.Models;
using Application = System.Windows.Application;
using ImageSource = System.Windows.Media.ImageSource;

namespace WindowsLosslessSwitcher.Services;

/// <summary>Which toast layout to render: 1c rich (artwork + transition + track) or 1d pill.</summary>
internal enum ToastVariant
{
    Rich,
    Pill,
}

/// <summary>Everything a toast window needs to render either variant.</summary>
internal sealed record SwitchToastContent(
    ToastVariant Variant,
    string Kicker,
    string? OldFormatText,
    string? NewFormatText,
    string? NewRateText,
    string? NewBitsText,
    string? TrackLine,
    string? DeviceName,
    ImageSource? Artwork,
    string? TrackUniqueKey = null);

public sealed class SwitchToastService : IDisposable
{
    // A skip-heavy session can enqueue cache updates faster than switch toasts let them drain;
    // beyond this the oldest are stale enough that showing them would just be noise.
    private const int MaxPendingFormatCacheUpdates = 3;

    private readonly List<FormatCacheToastRequest> _pendingFormatCacheUpdates = new();
    private readonly Func<SwitchToastContent, ISwitchToastWindow> _createToast;
    private readonly Func<bool> _checkAccess;
    private readonly Action<Action> _invoke;
    private ISwitchToastWindow? _currentToast;
    private ToastKind? _currentToastKind;
    private string? _currentToastTrackKey;
    private bool _disposed;

    public SwitchToastService()
        : this(
            content => new Views.SwitchToastWindow(content),
            () => Application.Current.Dispatcher.CheckAccess(),
            action => Application.Current.Dispatcher.Invoke(action))
    {
    }

    internal SwitchToastService(
        Func<SwitchToastContent, ISwitchToastWindow> createToast,
        Func<bool> checkAccess,
        Action<Action> invoke)
    {
        _createToast = createToast;
        _checkAccess = checkAccess;
        _invoke = invoke;
    }

    /// <summary>
    /// The standard switched-format toast. Variant follows the metadata setting: rich card
    /// with the old→new transition and track line when on, minimal pill when off.
    /// </summary>
    public void ShowSwitchedFormat(
        string? deviceName,
        AudioFormatCandidate? previousFormat,
        AudioFormatCandidate appliedFormat,
        TrackSnapshot? track,
        ImageSource? artwork,
        bool includeMetadata)
    {
        var content = BuildFormatContent(
            "LOSSLESS SWITCH", previousFormat, appliedFormat, track, deviceName, artwork, includeMetadata);
        RunOnDispatcher(() => ShowSwitchToastCore(content));
    }

    /// <summary>
    /// Rate-undetermined fallback notification — always the rich layout, because the pill
    /// cannot carry the explanation. Same replacement semantics as a switch toast.
    /// </summary>
    public void ShowRateUndetermined(
        string? deviceName,
        AudioFormatCandidate appliedFormat,
        ImageSource? artwork,
        TrackSnapshot? track = null)
    {
        var content = new SwitchToastContent(
            ToastVariant.Rich,
            "RATE UNDETERMINED",
            null,
            AudioFormatTextFormatter.Format(appliedFormat),
            AudioFormatTextFormatter.FormatSampleRate(appliedFormat.SampleRateHz),
            $"{appliedFormat.BitDepth}-bit",
            "Apple Music didn't report this track's rate — using a safe fallback.",
            deviceName,
            artwork,
            track?.UniqueKey);
        RunOnDispatcher(() => ShowSwitchToastCore(content));
    }

    /// <summary>
    /// Delivers late-loading artwork into the currently visible toast when it belongs to the
    /// same track. GSMTC artwork routinely arrives seconds after the track change — often after
    /// the toast is already on screen.
    /// </summary>
    public void UpdateToastArtwork(string? trackKey, ImageSource? artwork)
    {
        if (trackKey is null || artwork is null)
        {
            return;
        }

        RunOnDispatcher(() =>
        {
            if (!_disposed &&
                _currentToast is { } toast &&
                string.Equals(_currentToastTrackKey, trackKey, StringComparison.Ordinal))
            {
                toast.UpdateArtwork(artwork);
            }
        });
    }

    public void ShowFormatCacheUpdated(
        string? deviceName,
        AudioFormatCandidate previousFormat,
        AudioFormatCandidate updatedFormat,
        TrackSnapshot? track,
        ImageSource? artwork,
        bool includeMetadata)
    {
        var content = BuildFormatContent(
            "FORMAT UPDATED FOR NEXT PLAYBACK", previousFormat, updatedFormat, track, deviceName, artwork, includeMetadata);
        var request = new FormatCacheToastRequest(content, track?.UniqueKey);
        RunOnDispatcher(() => ShowFormatCacheUpdatedCore(request));
    }

    public void DiscardPendingFormatCacheUpdates() => RunOnDispatcher(DiscardPendingFormatCacheUpdatesCore);

    public void Dispose() => RunOnDispatcher(DisposeCore);

    private void RunOnDispatcher(Action action)
    {
        if (_checkAccess())
        {
            action();
            return;
        }

        _invoke(action);
    }

    private static SwitchToastContent BuildFormatContent(
        string kicker,
        AudioFormatCandidate? previousFormat,
        AudioFormatCandidate appliedFormat,
        TrackSnapshot? track,
        string? deviceName,
        ImageSource? artwork,
        bool includeMetadata) =>
        new(
            includeMetadata ? ToastVariant.Rich : ToastVariant.Pill,
            kicker,
            previousFormat is null ? null : AudioFormatTextFormatter.Format(previousFormat),
            AudioFormatTextFormatter.Format(appliedFormat),
            AudioFormatTextFormatter.FormatSampleRate(appliedFormat.SampleRateHz),
            $"{appliedFormat.BitDepth}-bit",
            includeMetadata ? BuildTrackLine(track) : null,
            deviceName,
            artwork,
            track?.UniqueKey);

    private static string? BuildTrackLine(TrackSnapshot? track)
    {
        if (track is null)
        {
            return null;
        }

        var title = track.Title ?? "Unknown Title";
        return track.Artist is { Length: > 0 } artist ? $"{title} — {artist}" : title;
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

    private void ShowSwitchToastCore(SwitchToastContent content)
    {
        if (_disposed)
        {
            return;
        }

        var toast = CreateToast(ToastKind.Switch, content);

        // WPF raises Closed synchronously. Make the replacement current first so the old
        // window's handler cannot drain queued cache notifications between switch toasts.
        var previousToast = _currentToast;
        _currentToast = toast;
        _currentToastKind = ToastKind.Switch;
        _currentToastTrackKey = content.TrackUniqueKey;
        previousToast?.Close();
        ShowCurrentToast(toast);
    }

    private void ShowFormatCacheToast(FormatCacheToastRequest request)
    {
        var toast = CreateToast(ToastKind.FormatCacheUpdate, request.Content);
        _currentToast = toast;
        _currentToastKind = ToastKind.FormatCacheUpdate;
        _currentToastTrackKey = request.Content.TrackUniqueKey;
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
        var key = request.TrackKey;
        if (key is not null)
        {
            _pendingFormatCacheUpdates.RemoveAll(pending =>
                string.Equals(pending.TrackKey, key, StringComparison.Ordinal));
        }

        _pendingFormatCacheUpdates.Add(request);
        while (_pendingFormatCacheUpdates.Count > MaxPendingFormatCacheUpdates)
        {
            _pendingFormatCacheUpdates.RemoveAt(0);
        }
    }

    private ISwitchToastWindow CreateToast(ToastKind kind, SwitchToastContent content)
    {
        var toast = _createToast(content);
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

    private enum ToastKind
    {
        Switch,
        FormatCacheUpdate,
    }

    private sealed record FormatCacheToastRequest(SwitchToastContent Content, string? TrackKey);
}

internal interface ISwitchToastWindow
{
    event EventHandler? Closed;

    void Show();

    void Close();

    void StartAutoClose();

    void UpdateArtwork(ImageSource artwork);
}
