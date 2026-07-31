using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsLosslessSwitcher.Abstractions;
using Timer = System.Threading.Timer;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Owns the Apple-Music-only spectrograph pipeline: process-loopback capture of the
/// <c>AMPLibraryAgent</c> process tree (the process that actually renders Apple Music audio —
/// same ground truth the switching coordinator uses) feeding a <see cref="SpectrumAnalyzer"/>.
///
/// Lifecycle: capture runs only while at least one visible spectrograph holds a lease AND
/// playback isn't idle. Every device format switch invalidates the stream — restarts are the
/// steady state, driven by exponential backoff with per-restart PID re-resolution (the recovery
/// ladder can respawn the agent under a new PID). A watchdog restarts capture when audio is
/// reported playing but the capture stays silent (PID reuse, process-topology change). Three
/// consecutive non-routine failures latch the monitor faulted for this visibility session and
/// the UI falls back to the static glyph.
/// </summary>
public sealed class AppleMusicAudioMonitor : ISpectrumSource, IDisposable
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PidRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthyThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlaybackIdleThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WatchdogSilence = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WatchdogCooldown = TimeSpan.FromSeconds(10);
    private const double ActiveSilenceCutoffSeconds = 1.5;

    private readonly DiagnosticsLogger _logger;
    private readonly IMediaTransportController _transport;
    private readonly ISpectrographCaptureFactory _captureFactory;
    private readonly Func<int?> _pidResolver;
    private readonly SpectrumAnalyzer _analyzer = new();
    private readonly object _sync = new();

    private int _leaseCount;
    private ISpectrographCaptureSession? _session;
    private Timer? _retryTimer;
    private Timer? _pollTimer;
    private int _consecutiveFailures;
    private bool _faulted;
    private bool _faultLogged;
    private bool _playbackIdle;
    private bool _disposed;
    private TimeSpan _backoff = MinBackoff;
    private DateTimeOffset _sessionStartedAt;
    private DateTimeOffset _lastPlayingAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWatchdogRestartAt = DateTimeOffset.MinValue;
    private volatile bool _capturing;

    public AppleMusicAudioMonitor(
        DiagnosticsLogger logger,
        IMediaTransportController transport)
        : this(logger, transport, new ProcessLoopbackCaptureFactory(), ResolveAppleMusicAudioPid)
    {
    }

    internal AppleMusicAudioMonitor(
        DiagnosticsLogger logger,
        IMediaTransportController transport,
        ISpectrographCaptureFactory captureFactory,
        Func<int?> pidResolver)
    {
        _logger = logger;
        _transport = transport;
        _captureFactory = captureFactory;
        _pidResolver = pidResolver;
    }

    public float[] CurrentBars => _analyzer.LatestBars;

    public bool IsActive => _capturing && _analyzer.SecondsSinceNonSilent < ActiveSilenceCutoffSeconds;

    public IDisposable AcquireVisibleLease()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return EmptyLease.Instance;
            }

            if (++_leaseCount == 1)
            {
                // Fresh visibility session gets a fresh chance even after an earlier fault.
                _faulted = false;
                _consecutiveFailures = 0;
                _backoff = MinBackoff;
                _playbackIdle = false;
                _pollTimer ??= new Timer(_ => OnPollTick(), null, Timeout.Infinite, Timeout.Infinite);
                _pollTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
                EnsureCaptureStartedLocked();
            }
        }

        return new Lease(this);
    }

    private void ReleaseLease()
    {
        lock (_sync)
        {
            if (_disposed || _leaseCount == 0)
            {
                return;
            }

            if (--_leaseCount == 0)
            {
                StopCaptureLocked();
                _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _retryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _analyzer.Reset();
            }
        }
    }

    private void EnsureCaptureStartedLocked()
    {
        if (_disposed || _faulted || _playbackIdle || _leaseCount == 0 || _session is not null)
        {
            return;
        }

        var pid = _pidResolver();
        if (pid is null)
        {
            ScheduleRetryLocked(PidRetryInterval);
            return;
        }

        try
        {
            var session = _captureFactory.Create(pid.Value, _analyzer.ProcessSamples, OnStreamFailed);
            session.Start();
            _session = session;
            _sessionStartedAt = DateTimeOffset.UtcNow;
            _capturing = true;
            _logger.Verbose($"Spectrograph capture started for PID {pid.Value}.");
        }
        catch (Exception ex)
        {
            HandleFailureLocked(ex);
        }
    }

    private void StopCaptureLocked()
    {
        _capturing = false;
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    private void OnStreamFailed(Exception exception)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            StopCaptureLocked();
            HandleFailureLocked(exception);
        }
    }

    private void HandleFailureLocked(Exception exception)
    {
        // Device/resource invalidation is the routine consequence of this app's own format
        // switches; a session that ran healthily for a while also doesn't indicate a fault.
        var routine = exception is COMException com &&
            (com.HResult == ProcessLoopbackInterop.AudclntErrDeviceInvalidated ||
             com.HResult == ProcessLoopbackInterop.AudclntErrResourcesInvalidated);
        var wasHealthy = DateTimeOffset.UtcNow - _sessionStartedAt > HealthyThreshold;

        if (routine || wasHealthy)
        {
            _consecutiveFailures = 0;
            _backoff = MinBackoff;
        }
        else if (++_consecutiveFailures >= 3)
        {
            _faulted = true;
            if (!_faultLogged)
            {
                _faultLogged = true;
                _logger.Warn($"Spectrograph capture disabled after repeated failures: {exception.Message}");
            }

            return;
        }

        _logger.Verbose($"Spectrograph capture interrupted ({exception.Message}); retrying in {_backoff.TotalMilliseconds:0} ms.");
        ScheduleRetryLocked(_backoff);
        _backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, _backoff.Ticks * 2));
    }

    private void ScheduleRetryLocked(TimeSpan delay)
    {
        _retryTimer ??= new Timer(_ => OnRetryTick(), null, Timeout.Infinite, Timeout.Infinite);
        _retryTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnRetryTick()
    {
        lock (_sync)
        {
            EnsureCaptureStartedLocked();
        }
    }

    private void OnPollTick()
    {
        bool playing;
        try
        {
            playing = _transport.GetPlaybackState().IsPlaying;
        }
        catch
        {
            playing = false;
        }

        lock (_sync)
        {
            if (_disposed || _leaseCount == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (playing)
            {
                _lastPlayingAt = now;
                if (_playbackIdle)
                {
                    _playbackIdle = false;
                    EnsureCaptureStartedLocked();
                }

                // Watchdog: playing but the capture hears nothing → wrong PID or a silently dead
                // stream. Re-resolve and restart, at most once per cooldown.
                if (_session is not null &&
                    _analyzer.SecondsSinceNonSilent > WatchdogSilence.TotalSeconds &&
                    now - _sessionStartedAt > WatchdogSilence &&
                    now - _lastWatchdogRestartAt > WatchdogCooldown)
                {
                    _lastWatchdogRestartAt = now;
                    _logger.Verbose("Spectrograph watchdog: playback reported but capture silent; restarting capture.");
                    StopCaptureLocked();
                    EnsureCaptureStartedLocked();
                }
            }
            else if (!_playbackIdle &&
                     _session is not null &&
                     _lastPlayingAt != DateTimeOffset.MinValue &&
                     now - _lastPlayingAt > PlaybackIdleThreshold)
            {
                // Paused for a while with the window open: stop capture, keep leases; the next
                // Playing poll restarts instantly.
                _playbackIdle = true;
                _logger.Verbose("Spectrograph capture paused (playback idle).");
                StopCaptureLocked();
            }
            else if (_session is null && !_playbackIdle && _lastPlayingAt == DateTimeOffset.MinValue)
            {
                // Never seen playback and no session (e.g. Apple Music not running): keep trying
                // via the retry timer only; nothing to do here.
            }
        }
    }

    /// <summary>
    /// Production PID ladder: the newest AMPLibraryAgent (Apple's media agent owns the render
    /// session and survives Apple Music restarts), falling back to AppleMusic.exe in case a
    /// future update moves rendering in-process.
    /// </summary>
    internal static int? ResolveAppleMusicAudioPid()
    {
        var pid = FindNewestProcessId("AMPLibraryAgent");
        return pid ?? FindNewestProcessId("AppleMusic");
    }

    private static int? FindNewestProcessId(string processName)
    {
        int? bestPid = null;
        var bestStart = DateTime.MinValue;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var started = process.StartTime;
                if (bestPid is null || started > bestStart)
                {
                    bestPid = process.Id;
                    bestStart = started;
                }
            }
            catch
            {
                // Access denied or the process exited between enumeration and inspection.
                bestPid ??= process.Id;
            }
            finally
            {
                process.Dispose();
            }
        }

        return bestPid;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCaptureLocked();
            _retryTimer?.Dispose();
            _pollTimer?.Dispose();
        }
    }

    private sealed class Lease : IDisposable
    {
        private AppleMusicAudioMonitor? _owner;

        public Lease(AppleMusicAudioMonitor owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseLease();
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        public static readonly EmptyLease Instance = new();

        public void Dispose()
        {
        }
    }
}
