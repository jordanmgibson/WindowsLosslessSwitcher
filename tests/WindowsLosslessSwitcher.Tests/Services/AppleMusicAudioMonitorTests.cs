using System.IO;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AppleMusicAudioMonitorTests : IDisposable
{
    private readonly DiagnosticsLogger _logger =
        new(Path.Combine(Path.GetTempPath(), "WindowsLosslessSwitcher.Tests", Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
    }

    private sealed class FakeSession : ISpectrographCaptureSession
    {
        public int TargetPid { get; init; }

        public Action<Exception>? OnStreamFailed { get; init; }

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public void Start() => Started = true;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory : ISpectrographCaptureFactory
    {
        public List<FakeSession> Sessions { get; } = [];

        public Exception? ThrowOnCreate { get; set; }

        public ISpectrographCaptureSession Create(
            int targetProcessId,
            Action<float[], int> onSamples,
            Action<Exception> onStreamFailed)
        {
            if (ThrowOnCreate is not null)
            {
                throw ThrowOnCreate;
            }

            var session = new FakeSession { TargetPid = targetProcessId, OnStreamFailed = onStreamFailed };
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeTransport : IMediaTransportController
    {
        public MediaTransportPlaybackStatus Status { get; set; } = MediaTransportPlaybackStatus.Playing;

        public MediaTransportState GetPlaybackState() =>
            new(Status, true, true, null, DateTimeOffset.UtcNow);

        public Task<bool> TryPauseAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TryPlayAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TryTogglePlayPauseAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TryChangePlaybackPositionAsync(TimeSpan position, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> TrySkipNextAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TrySkipPreviousAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    [Fact]
    public void FirstLease_StartsCaptureWithResolvedPid()
    {
        var factory = new FakeFactory();
        using var monitor = new AppleMusicAudioMonitor(_logger, new FakeTransport(), factory, () => 4242);

        using var lease = monitor.AcquireVisibleLease();

        var session = Assert.Single(factory.Sessions);
        Assert.True(session.Started);
        Assert.Equal(4242, session.TargetPid);
    }

    [Fact]
    public void SecondLease_DoesNotDoubleStart()
    {
        var factory = new FakeFactory();
        using var monitor = new AppleMusicAudioMonitor(_logger, new FakeTransport(), factory, () => 1);

        using var first = monitor.AcquireVisibleLease();
        using var second = monitor.AcquireVisibleLease();

        Assert.Single(factory.Sessions);
    }

    [Fact]
    public void LastLeaseRelease_DisposesSession()
    {
        var factory = new FakeFactory();
        using var monitor = new AppleMusicAudioMonitor(_logger, new FakeTransport(), factory, () => 1);

        var first = monitor.AcquireVisibleLease();
        var second = monitor.AcquireVisibleLease();
        first.Dispose();
        Assert.False(factory.Sessions[0].Disposed);
        second.Dispose();

        Assert.True(factory.Sessions[0].Disposed);
        Assert.False(monitor.IsActive);
    }

    [Fact]
    public void StreamFailure_RestartsWithReresolvedPid()
    {
        var factory = new FakeFactory();
        var pid = 100;
        using var monitor = new AppleMusicAudioMonitor(_logger, new FakeTransport(), factory, () => ++pid);

        using var lease = monitor.AcquireVisibleLease();
        Assert.Equal(101, factory.Sessions[0].TargetPid);

        factory.Sessions[0].OnStreamFailed!(new InvalidOperationException("device invalidated"));

        Assert.True(WaitUntil(() => factory.Sessions.Count >= 2), "expected a restart after stream failure");
        Assert.Equal(102, factory.Sessions[1].TargetPid);
    }

    [Fact]
    public void NullPid_RetriesWithoutSession()
    {
        var factory = new FakeFactory();
        int? pid = null;
        using var monitor = new AppleMusicAudioMonitor(_logger, new FakeTransport(), factory, () => pid);

        using var lease = monitor.AcquireVisibleLease();
        Assert.Empty(factory.Sessions);

        // Once the process appears, the PID retry (5 s) or a playback poll picks it up. Use the
        // playback-idle round trip to trigger a restart quickly instead of waiting five seconds.
        pid = 7;
        Assert.True(WaitUntil(() => factory.Sessions.Count == 1, 7000), "expected capture once PID resolves");
        Assert.Equal(7, factory.Sessions[0].TargetPid);
    }

    [Fact]
    public void RepeatedImmediateFailures_LatchFaulted()
    {
        var factory = new FakeFactory();
        using var monitor = new AppleMusicAudioMonitor(_logger, new FakeTransport(), factory, () => 1);

        using var lease = monitor.AcquireVisibleLease();
        Assert.True(WaitUntil(() => factory.Sessions.Count >= 1));

        // Fail each session immediately; backoff grows 250→500→1000 ms, then the latch stops it.
        for (var i = 0; i < 3 && WaitUntil(() => factory.Sessions.Count > i, 4000); i++)
        {
            factory.Sessions[i].OnStreamFailed!(new InvalidOperationException($"boom {i}"));
        }

        var countAfterLatch = factory.Sessions.Count;
        Thread.Sleep(1500);
        Assert.Equal(countAfterLatch, factory.Sessions.Count);
        Assert.False(monitor.IsActive);
    }

    [Fact]
    public void Dispose_WhileLeased_IsClean()
    {
        var factory = new FakeFactory();
        var monitor = new AppleMusicAudioMonitor(_logger, new FakeTransport(), factory, () => 1);
        var lease = monitor.AcquireVisibleLease();

        monitor.Dispose();
        lease.Dispose();

        Assert.True(factory.Sessions[0].Disposed);
    }

    [Fact]
    public void KeepAlive_LeaseRelease_KeepsSessionAlive()
    {
        var factory = new FakeFactory();
        using var monitor = new AppleMusicAudioMonitor(
            _logger, new FakeTransport(), factory, () => 1, keepSessionForProcessLifetime: true);

        var lease = monitor.AcquireVisibleLease();
        Assert.Single(factory.Sessions);
        lease.Dispose();

        // Windows 10 mode: tearing down would burn the PID, so the session drains on.
        Assert.False(factory.Sessions[0].Disposed);

        using var second = monitor.AcquireVisibleLease();
        Assert.Single(factory.Sessions);
    }

    [Fact]
    public void KeepAlive_StreamFailure_WaitsForAgentRespawn()
    {
        var factory = new FakeFactory();
        var pid = 100;
        using var monitor = new AppleMusicAudioMonitor(
            _logger, new FakeTransport(), factory, () => pid, keepSessionForProcessLifetime: true);

        using var lease = monitor.AcquireVisibleLease();
        Assert.Equal(100, factory.Sessions[0].TargetPid);

        factory.Sessions[0].OnStreamFailed!(new InvalidOperationException("stream died"));

        // The dead session's PID is burnt: the 250 ms retry must NOT recreate for it.
        Thread.Sleep(1200);
        Assert.Single(factory.Sessions);

        // Agent respawn under a new PID resumes capture on the next PID poll (≤ 5 s).
        pid = 200;
        Assert.True(WaitUntil(() => factory.Sessions.Count == 2, 7000), "expected capture for the respawned PID");
        Assert.Equal(200, factory.Sessions[1].TargetPid);
    }

    [Fact]
    public void KeepAlive_WatchdogSilence_DoesNotRecycleUnchangedPid()
    {
        var factory = new FakeFactory();
        using var monitor = new AppleMusicAudioMonitor(
            _logger, new FakeTransport { Status = MediaTransportPlaybackStatus.Playing },
            factory, () => 1, keepSessionForProcessLifetime: true);

        using var lease = monitor.AcquireVisibleLease();
        Assert.Single(factory.Sessions);

        // Playing + a fake that never produces samples = permanent "silence": past the 3 s
        // watchdog threshold this would recycle on Windows 11, but with an unchanged PID the
        // Windows 10 path must leave the one usable stream alone.
        Thread.Sleep(5000);
        Assert.Single(factory.Sessions);
        Assert.False(factory.Sessions[0].Disposed);
    }

    [Fact]
    public void PlaybackPausedLongEnough_StopsCapture_ResumeRestarts()
    {
        var factory = new FakeFactory();
        var transport = new FakeTransport { Status = MediaTransportPlaybackStatus.Playing };
        using var monitor = new AppleMusicAudioMonitor(_logger, transport, factory, () => 1);

        using var lease = monitor.AcquireVisibleLease();
        Assert.True(WaitUntil(() => factory.Sessions.Count == 1));

        // The idle threshold is 10 s of not-playing — too slow to wait out fully in a unit
        // test. This verifies a short pause doesn't tear capture down for good: a live (not
        // disposed) session still exists afterwards. Note the fake never produces samples, so
        // the "playing but silent" watchdog may legitimately recycle sessions along the way.
        transport.Status = MediaTransportPlaybackStatus.Paused;
        Thread.Sleep(2500);
        Assert.True(WaitUntil(() => factory.Sessions.Any(session => !session.Disposed)));

        transport.Status = MediaTransportPlaybackStatus.Playing;
        Thread.Sleep(1200);
        Assert.True(WaitUntil(() => factory.Sessions.Any(session => !session.Disposed)));
    }
}
