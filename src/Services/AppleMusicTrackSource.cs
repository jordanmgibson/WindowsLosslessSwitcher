using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class AppleMusicTrackSource : ITrackSource, IMediaTransportController
{
    // AUMID prefix shared by all Apple Music for Windows package identities.
    private const string AppleMusicAumidPrefix = "AppleInc.AppleMusicWin";

    // Apple Music briefly emits "Connecting…" metadata while it resolves the track after a skip
    // or app resume. Debouncing for 2 seconds prevents a spurious format switch when the real
    // track metadata arrives within that window.
    private static readonly TimeSpan PlaceholderDebounce = TimeSpan.FromSeconds(2);

    // The GSMTC session can disappear for a moment during Apple Music restarts or system
    // sleep/wake cycles. Waiting 3 seconds before fully detaching avoids losing the session
    // during a transient blip.
    private static readonly TimeSpan SessionLossDebounce = TimeSpan.FromSeconds(3);

    private readonly DiagnosticsLogger _logger;
    private readonly TimeSpan _placeholderDebounce;
    private readonly TimeSpan _sessionLossDebounce;
    private readonly object _sessionStateSync = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _attachedSession;
    private string? _lastTrackKey;
    private CancellationTokenSource? _placeholderPublishCts;
    private CancellationTokenSource? _sessionLossCts;
    private TrackSnapshot? _lastRealTrack;
    private MediaSessionSnapshot _sessionSnapshot = MediaSessionSnapshot.CreateUnavailable();
    private MediaArtworkSnapshot _artworkSnapshot = MediaArtworkSnapshot.CreateUnavailable();
    private long _snapshotGeneration;

    public AppleMusicTrackSource(DiagnosticsLogger logger)
        : this(logger, PlaceholderDebounce, SessionLossDebounce)
    {
    }

    internal AppleMusicTrackSource(
        DiagnosticsLogger logger,
        TimeSpan placeholderDebounce,
        TimeSpan sessionLossDebounce)
    {
        _logger = logger;
        _placeholderDebounce = placeholderDebounce;
        _sessionLossDebounce = sessionLossDebounce;
    }

    public event EventHandler<TrackSnapshot>? TrackChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += OnSessionsChanged;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        await RefreshAttachedSessionAsync();
        _logger.Info("Apple Music track source started.");
    }

    public async ValueTask DisposeAsync()
    {
        CancelSessionLossDebounce();
        CancelPlaceholderPublish();
        await DetachSessionAsync();
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
    }

    public MediaTransportState GetPlaybackState()
    {
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return MediaTransportState.CreateUnavailable();
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playbackInfo.Controls;

            return new MediaTransportState(
                MapPlaybackStatus(playbackInfo.PlaybackStatus),
                controls?.IsPauseEnabled ?? false,
                controls?.IsPlayEnabled ?? false,
                timeline.Position,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            return MediaTransportState.CreateUnavailable();
        }
    }

    public MediaSessionSnapshot GetSessionSnapshot()
    {
        lock (_sessionStateSync)
        {
            return _sessionSnapshot;
        }
    }

    public MediaArtworkSnapshot GetArtworkSnapshot()
    {
        lock (_sessionStateSync)
        {
            return _artworkSnapshot;
        }
    }

    public async Task<bool> TryPauseAsync(CancellationToken cancellationToken)
    {
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPauseEnabled != true)
            {
                return false;
            }

            return await session.TryPauseAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to pause Apple Music playback: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryPlayAsync(CancellationToken cancellationToken)
    {
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPlayEnabled != true)
            {
                return false;
            }

            return await session.TryPlayAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to resume Apple Music playback: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryTogglePlayPauseAsync(CancellationToken cancellationToken)
    {
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPlayPauseToggleEnabled != true)
            {
                return false;
            }

            return await session.TryTogglePlayPauseAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to toggle Apple Music playback: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryChangePlaybackPositionAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPlaybackPositionEnabled != true)
            {
                return false;
            }

            return await session.TryChangePlaybackPositionAsync(position.Ticks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to change Apple Music playback position: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TrySkipNextAsync(CancellationToken cancellationToken)
    {
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsNextEnabled != true)
            {
                return false;
            }

            return await session.TrySkipNextAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to skip Apple Music to the next track: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TrySkipPreviousAsync(CancellationToken cancellationToken)
    {
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPreviousEnabled != true)
            {
                return false;
            }

            // Apple Music's skip-previous RESTARTS the current track when playback is far enough
            // in (verified live: restart at 0:20 and 2:41); callers guard the position so this
            // never jumps to the previous track.
            return await session.TrySkipPreviousAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to restart the current Apple Music track: {ex.Message}");
            return false;
        }
    }

    private async void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        await RefreshAttachedSessionAsync();
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        await RefreshAttachedSessionAsync();
    }

    private async Task RefreshAttachedSessionAsync()
    {
        if (_manager is null)
        {
            return;
        }

        var sessions = _manager.GetSessions().ToList();
        _logger.Info(
            $"GSMTC sessions visible: {sessions.Count}. " +
            $"{string.Join(", ", sessions.Select(session => session.SourceAppUserModelId))}");

        var candidate = SelectPreferredSession(sessions, _manager.GetCurrentSession(), _attachedSession);

        if (candidate is not null)
        {
            CancelSessionLossDebounce();
        }

        if (ReferenceEquals(candidate, _attachedSession))
        {
            return;
        }

        if (candidate is null)
        {
            BeginSessionLossDebounce();
            return;
        }

        await DetachSessionAsync();
        _attachedSession = candidate;
        _logger.Info($"Attaching Apple Music session {candidate.SourceAppUserModelId}. lastTrack={_lastTrackKey ?? "none"}.");
        _attachedSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _attachedSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
        await RefreshSessionFromPropertiesAsync(_attachedSession, "session-attached", publishTrackSnapshot: true);
    }

    private Task DetachSessionAsync()
    {
        if (_attachedSession is not null)
        {
            _logger.Info($"Detaching Apple Music session {_attachedSession.SourceAppUserModelId}. lastTrack={_lastTrackKey ?? "none"}.");
            _attachedSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _attachedSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _attachedSession = null;
        UpdateSessionState(MediaSessionSnapshot.CreateUnavailable(), MediaArtworkSnapshot.CreateUnavailable());
        return Task.CompletedTask;
    }

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _logger.Info($"Apple Music media-properties callback received from {sender.SourceAppUserModelId}.");
        await RefreshSessionFromPropertiesAsync(sender, "media-properties", publishTrackSnapshot: true);
    }

    private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
        try
        {
            status = sender.GetPlaybackInfo().PlaybackStatus;
        }
        catch
        {
            UpdateSessionFromPlaybackInfo(sender);
            return;
        }

        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            _logger.Info($"Apple Music playback-playing callback received from {sender.SourceAppUserModelId}.");
            await RefreshSessionFromPropertiesAsync(sender, "playback-playing", publishTrackSnapshot: true);
            return;
        }

        UpdateSessionFromPlaybackInfo(sender);
    }

    private async Task RefreshSessionFromPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session,
        string reason,
        bool publishTrackSnapshot)
    {
        var callbackStopwatch = Stopwatch.StartNew();
        GlobalSystemMediaTransportControlsSessionMediaProperties properties;
        try
        {
            properties = await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to read Apple Music media properties ({reason}): {ex.Message}");
            return;
        }

        var snapshot = BuildTrackSnapshot(session, properties, reason);
        snapshot = NormalizeSnapshot(snapshot);

        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo;
        try
        {
            playbackInfo = session.GetPlaybackInfo();
        }
        catch
        {
            UpdateSessionFromPlaybackInfo(session);
            return;
        }

        var sessionSnapshot = BuildSessionSnapshot(session, snapshot, playbackInfo, MediaArtworkSnapshot.CreateUnavailable());
        _ = ProcessResolvedSnapshotAsync(
            snapshot,
            sessionSnapshot,
            _ => ReadArtworkSnapshotAsync(properties, CancellationToken.None),
            publishTrackSnapshot,
            reason,
            callbackStopwatch.ElapsedMilliseconds,
            CancellationToken.None);
    }

    internal async Task ProcessResolvedSnapshotAsync(
        TrackSnapshot snapshot,
        MediaSessionSnapshot sessionSnapshot,
        Func<CancellationToken, Task<MediaArtworkSnapshot>> artworkLoader,
        bool publishTrackSnapshot,
        string reason,
        long callbackLatencyMs,
        CancellationToken cancellationToken)
    {
        if (publishTrackSnapshot)
        {
            _logger.Info(
                $"Publishing Apple Music track snapshot after {callbackLatencyMs} ms ({reason}).",
                new DiagnosticsLogger.LogCorrelation(TrackKey: snapshot.UniqueKey));
            HandleSnapshot(snapshot, isNormalized: true);
        }

        UpdateSessionState(sessionSnapshot, MediaArtworkSnapshot.CreateUnavailable());

        try
        {
            var artworkSnapshot = await artworkLoader(cancellationToken);
            ApplyArtworkSnapshotIfCurrent(snapshot, sessionSnapshot, artworkSnapshot, reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to refresh Apple Music artwork ({reason}): {ex.Message}");
        }
    }

    internal void HandleSnapshot(TrackSnapshot snapshot)
    {
        snapshot = NormalizeSnapshot(snapshot);
        HandleSnapshot(snapshot, isNormalized: true);
    }

    internal static bool IsPlaceholderTrack(TrackSnapshot snapshot)
    {
        return IsPlaceholderValue(snapshot.Title) || IsPlaceholderValue(snapshot.Artist);
    }

    private void HandleSnapshot(TrackSnapshot snapshot, bool isNormalized)
    {
        if (!isNormalized)
        {
            snapshot = NormalizeSnapshot(snapshot);
        }

        var snapshotGeneration = Interlocked.Increment(ref _snapshotGeneration);
        var correlation = CreateSnapshotCorrelation(snapshotGeneration, snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.Title) && string.IsNullOrWhiteSpace(snapshot.Artist))
        {
            _logger.Info("Ignoring empty Apple Music snapshot.", correlation);
            return;
        }

        if (IsPlaceholderTrack(snapshot))
        {
            if (_lastRealTrack is not null)
            {
                _logger.Info($"Suppressing placeholder Apple Music snapshot while preserving last real track: {snapshot.Title} ({snapshot.DetectionReason}).", correlation);
                return;
            }

            SchedulePlaceholderPublish(snapshot, snapshotGeneration);
            return;
        }

        CancelPlaceholderPublish();
        _lastRealTrack = snapshot;
        PublishSnapshot(snapshot, snapshot.DetectionReason);
    }

    private void UpdateSessionFromPlaybackInfo(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var currentSnapshot = GetSessionSnapshot();
            if (string.IsNullOrWhiteSpace(currentSnapshot.SourceAppUserModelId))
            {
                return;
            }

            var updatedSnapshot = currentSnapshot with
            {
                PlaybackStatus = MapPlaybackStatus(playbackInfo.PlaybackStatus),
                CanPause = playbackInfo.Controls?.IsPauseEnabled ?? false,
                CanPlay = playbackInfo.Controls?.IsPlayEnabled ?? false,
                CanGoNext = playbackInfo.Controls?.IsNextEnabled ?? false,
                CanGoPrevious = playbackInfo.Controls?.IsPreviousEnabled ?? false,
                CanShuffle = playbackInfo.Controls?.IsShuffleEnabled ?? false,
                CanRepeat = playbackInfo.Controls?.IsRepeatEnabled ?? false,
                IsShuffleActive = playbackInfo.IsShuffleActive,
                RepeatMode = MapRepeatMode(playbackInfo.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None),
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };

            UpdateSessionState(updatedSnapshot, GetArtworkSnapshot());
        }
        catch
        {
        }
    }

    private static TrackSnapshot BuildTrackSnapshot(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties properties,
        string reason) =>
        new(
            session.SourceAppUserModelId,
            null,
            properties.Title,
            properties.Artist,
            properties.AlbumTitle,
            reason,
            DateTimeOffset.UtcNow);

    private TrackSnapshot NormalizeSnapshot(TrackSnapshot snapshot)
    {
        var rawSnapshot = snapshot;
        snapshot = AppleMusicTrackMetadataNormalizer.NormalizeSnapshot(snapshot);
        if (rawSnapshot.Title != snapshot.Title ||
            rawSnapshot.Artist != snapshot.Artist ||
            rawSnapshot.Album != snapshot.Album)
        {
            _logger.Info(
                $"Normalized Apple Music metadata: raw=({rawSnapshot.Title} | {rawSnapshot.Artist} | {rawSnapshot.Album}), " +
                $"normalized=({snapshot.Title} | {snapshot.Artist} | {snapshot.Album}).");
        }

        if (ShouldLogIncompleteMetadata(rawSnapshot, snapshot))
        {
            _logger.Info(
                $"Observed irregular Apple Music metadata: raw=({rawSnapshot.Title} | {rawSnapshot.Artist} | {rawSnapshot.Album}), " +
                $"normalized=({snapshot.Title} | {snapshot.Artist} | {snapshot.Album}).",
                new DiagnosticsLogger.LogCorrelation(TrackKey: snapshot.UniqueKey));
        }

        return snapshot;
    }

    private static bool IsPlaceholderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Equals("Connecting…", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Connecting...", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Connecting", StringComparison.OrdinalIgnoreCase);
    }

    private void SchedulePlaceholderPublish(TrackSnapshot snapshot, long generation)
    {
        CancelPlaceholderPublish();

        var cts = new CancellationTokenSource();
        _placeholderPublishCts = cts;
        _logger.Info($"Deferring placeholder Apple Music snapshot: {snapshot.Title} ({snapshot.DetectionReason}).", CreateSnapshotCorrelation(generation, snapshot));
        _ = PublishPlaceholderAfterDelayAsync(snapshot, generation, cts);
    }

    private async Task PublishPlaceholderAfterDelayAsync(TrackSnapshot snapshot, long generation, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_placeholderDebounce, cts.Token);
            if (generation != Volatile.Read(ref _snapshotGeneration))
            {
                _logger.Info($"Discarding stale placeholder Apple Music snapshot: {snapshot.Title} ({snapshot.DetectionReason}).", CreateSnapshotCorrelation(generation, snapshot));
                return;
            }

            PublishSnapshot(snapshot, snapshot.DetectionReason);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_placeholderPublishCts, cts))
            {
                _placeholderPublishCts = null;
            }

            cts.Dispose();
        }
    }

    private void BeginSessionLossDebounce()
    {
        if (_sessionLossCts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _sessionLossCts = cts;
        _logger.Info("Apple Music session disappeared; waiting briefly before detaching.");
        _ = HandleSessionLossAsync(cts);
    }

    private async Task HandleSessionLossAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_sessionLossDebounce, cts.Token);
            if (_manager is null)
            {
                return;
            }

            var candidate = _manager.GetSessions()
                .FirstOrDefault(session => session.SourceAppUserModelId.Contains(AppleMusicAumidPrefix, StringComparison.OrdinalIgnoreCase));

            if (candidate is not null)
            {
                await RefreshAttachedSessionAsync();
                return;
            }

            await DetachSessionAsync();
            _logger.Warn("Apple Music session was not found after debounce.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_sessionLossCts, cts))
            {
                _sessionLossCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelSessionLossDebounce()
    {
        _sessionLossCts?.Cancel();
    }

    private void CancelPlaceholderPublish()
    {
        _placeholderPublishCts?.Cancel();
    }

    private void PublishSnapshot(TrackSnapshot snapshot, string reason)
    {
        if (snapshot.UniqueKey == _lastTrackKey)
        {
            _logger.Info($"Suppressing duplicate Apple Music snapshot publish ({reason}).", CreateSnapshotCorrelation(Volatile.Read(ref _snapshotGeneration), snapshot));
            return;
        }

        _lastTrackKey = snapshot.UniqueKey;
        _logger.Info($"Track changed: {snapshot.Title} - {snapshot.Artist} ({reason}).", CreateSnapshotCorrelation(Volatile.Read(ref _snapshotGeneration), snapshot));
        TrackChanged?.Invoke(this, snapshot);
    }

    private static MediaSessionSnapshot BuildSessionSnapshot(
        GlobalSystemMediaTransportControlsSession session,
        TrackSnapshot snapshot,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo,
        MediaArtworkSnapshot artworkSnapshot) =>
        new(
            session.SourceAppUserModelId,
            snapshot.Title,
            snapshot.Artist,
            snapshot.Album,
            MapPlaybackStatus(playbackInfo.PlaybackStatus),
            playbackInfo.Controls?.IsPauseEnabled ?? false,
            playbackInfo.Controls?.IsPlayEnabled ?? false,
            playbackInfo.Controls?.IsNextEnabled ?? false,
            playbackInfo.Controls?.IsPreviousEnabled ?? false,
            playbackInfo.Controls?.IsShuffleEnabled ?? false,
            playbackInfo.Controls?.IsRepeatEnabled ?? false,
            playbackInfo.IsShuffleActive,
            MapRepeatMode(playbackInfo.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None),
            artworkSnapshot.Revision,
            DateTimeOffset.UtcNow);

    private static MediaTransportPlaybackStatus MapPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) =>
        status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaTransportPlaybackStatus.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaTransportPlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaTransportPlaybackStatus.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaTransportPlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaTransportPlaybackStatus.Playing,
            _ => MediaTransportPlaybackStatus.Unknown,
        };

    private static MediaTransportRepeatMode MapRepeatMode(MediaPlaybackAutoRepeatMode repeatMode) =>
        repeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaTransportRepeatMode.Off,
            MediaPlaybackAutoRepeatMode.Track => MediaTransportRepeatMode.One,
            MediaPlaybackAutoRepeatMode.List => MediaTransportRepeatMode.All,
            _ => MediaTransportRepeatMode.Unknown,
        };

    private async Task<MediaArtworkSnapshot> ReadArtworkSnapshotAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties properties,
        CancellationToken cancellationToken)
    {
        if (properties.Thumbnail is null)
        {
            return MediaArtworkSnapshot.CreateUnavailable();
        }

        try
        {
            using var randomAccessStream = await properties.Thumbnail.OpenReadAsync();
            if (randomAccessStream.Size == 0 || randomAccessStream.Size > int.MaxValue)
            {
                return MediaArtworkSnapshot.CreateUnavailable();
            }

            using var reader = new DataReader(randomAccessStream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)randomAccessStream.Size).AsTask(cancellationToken);
            var bytes = new byte[randomAccessStream.Size];
            reader.ReadBytes(bytes);
            if (bytes.Length == 0)
            {
                return MediaArtworkSnapshot.CreateUnavailable();
            }

            var revision = Convert.ToHexString(SHA256.HashData(bytes));
            return new MediaArtworkSnapshot(bytes, randomAccessStream.ContentType, revision, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to read Apple Music artwork: {ex.Message}");
            return MediaArtworkSnapshot.CreateUnavailable();
        }
    }

    private void UpdateSessionState(MediaSessionSnapshot sessionSnapshot, MediaArtworkSnapshot artworkSnapshot)
    {
        lock (_sessionStateSync)
        {
            _sessionSnapshot = sessionSnapshot;
            _artworkSnapshot = artworkSnapshot;
        }
    }

    private void ApplyArtworkSnapshotIfCurrent(
        TrackSnapshot snapshot,
        MediaSessionSnapshot sessionSnapshot,
        MediaArtworkSnapshot artworkSnapshot,
        string reason)
    {
        lock (_sessionStateSync)
        {
            if (!IsSameTrack(_sessionSnapshot, snapshot))
            {
                _logger.Info(
                    $"Skipping stale Apple Music artwork refresh ({reason}) for {snapshot.UniqueKey}.",
                    new DiagnosticsLogger.LogCorrelation(TrackKey: snapshot.UniqueKey));
                return;
            }

            _sessionSnapshot = sessionSnapshot with
            {
                ArtworkRevision = artworkSnapshot.Revision,
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
            _artworkSnapshot = artworkSnapshot;
        }
    }

    private static bool IsSameTrack(MediaSessionSnapshot sessionSnapshot, TrackSnapshot snapshot)
    {
        return string.Equals(sessionSnapshot.SourceAppUserModelId, snapshot.SourceAppUserModelId, StringComparison.Ordinal) &&
               string.Equals(sessionSnapshot.Title, snapshot.Title, StringComparison.Ordinal) &&
               string.Equals(sessionSnapshot.Artist, snapshot.Artist, StringComparison.Ordinal) &&
               string.Equals(sessionSnapshot.Album, snapshot.Album, StringComparison.Ordinal);
    }

    private static bool ShouldLogIncompleteMetadata(TrackSnapshot rawSnapshot, TrackSnapshot normalizedSnapshot)
    {
        return string.IsNullOrWhiteSpace(rawSnapshot.Artist) ||
               string.IsNullOrWhiteSpace(rawSnapshot.Album) ||
               string.IsNullOrWhiteSpace(normalizedSnapshot.Artist) ||
               string.IsNullOrWhiteSpace(normalizedSnapshot.Album);
    }

    private static DiagnosticsLogger.LogCorrelation CreateSnapshotCorrelation(long snapshotGeneration, TrackSnapshot snapshot) =>
        new(TrackKey: snapshot.UniqueKey, SnapshotGeneration: snapshotGeneration);

    internal GlobalSystemMediaTransportControlsSession? GetPreferredCommandSession()
    {
        var manager = _manager;
        if (manager is null)
        {
            return _attachedSession;
        }

        try
        {
            return SelectPreferredSession(manager.GetSessions(), manager.GetCurrentSession(), _attachedSession) ?? _attachedSession;
        }
        catch
        {
            return _attachedSession;
        }
    }

    internal static GlobalSystemMediaTransportControlsSession? SelectPreferredSession(
        IEnumerable<GlobalSystemMediaTransportControlsSession> sessions,
        GlobalSystemMediaTransportControlsSession? currentSession,
        GlobalSystemMediaTransportControlsSession? attachedSession)
    {
        var appleMusicSessions = sessions
            .Where(session => session.SourceAppUserModelId.Contains(AppleMusicAumidPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (appleMusicSessions.Count == 0)
        {
            return null;
        }

        foreach (var session in appleMusicSessions)
        {
            try
            {
                if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return session;
                }
            }
            catch
            {
            }
        }

        if (currentSession is not null &&
            currentSession.SourceAppUserModelId.Contains(AppleMusicAumidPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return appleMusicSessions.FirstOrDefault(session => ReferenceEquals(session, currentSession)) ??
                   appleMusicSessions.FirstOrDefault(session => session.SourceAppUserModelId == currentSession.SourceAppUserModelId) ??
                   currentSession;
        }

        if (attachedSession is not null)
        {
            return appleMusicSessions.FirstOrDefault(session => ReferenceEquals(session, attachedSession)) ??
                   appleMusicSessions.FirstOrDefault(session => session.SourceAppUserModelId == attachedSession.SourceAppUserModelId) ??
                   attachedSession;
        }

        return appleMusicSessions[0];
    }
}
