using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class SwitchingCoordinatorTests
{
    private static readonly AudioDeviceInfo DefaultDevice = new("default-device", "USB DAC", true);
    private static readonly AudioFormatCandidate CurrentFormat = new(44100, 16, 2);
    private static readonly AudioFormatCandidate FallbackFormat = new(48000, 24, 2);
    private static readonly AudioFormatCandidate LosslessFormat = new(96000, 24, 2);
    // Generous enough for the full switch path: sustained stream gate (1.5 s) + pause + modeled
    // post-pause hold (2.2 s) + release-stability window (0.75 s) + settle + restore observation.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    // Recovery tests run the real 4 s restore-observation window plus nudge cycles (~13 s worst case).
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TrackChange_DoesNotPauseDuringResolutionAndLeavesTransportAloneWhenResolverFails()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat]);
        var resolver = new BlockingResolver(mediaTransportController);
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var failedStatusTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        // Track-change handling mutes (when a target device is cached) but never pauses before
        // resolution: Apple Music's render stream must keep spinning up for a clean switch later.
        await resolver.ResolveStarted.WaitAsync(DefaultTimeout);
        Assert.Equal(0, mediaTransportController.PauseCallCount);
        Assert.Equal(0, resolver.PauseCallCountWhenResolveStarted);

        resolver.Release(null);

        var finalStatus = await failedStatusTask.WaitAsync(DefaultTimeout);
        Assert.Equal("No format resolver returned a result.", finalStatus.FailureReason);
        Assert.Equal(0, mediaTransportController.PlayCallCount);
        Assert.Equal(0, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task TrackChange_WhenResolvedFormatIsAlreadyActive_DoesNotTouchTransport()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat]);
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.Equal("Resolver: CatalogManifest", finalStatus.ResolverStatusText);
        Assert.Equal(CurrentFormat, finalStatus.AppliedFormat);
        Assert.Equal(0, mediaTransportController.PauseCallCount);
        Assert.Equal(0, mediaTransportController.PlayCallCount);
        Assert.Equal(0, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task TrackChange_WhenResolverReturnsNull_DoesNotTouchTransport()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat]);
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(null));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.Equal("No format resolver returned a result.", finalStatus.FailureReason);
        Assert.Equal(0, mediaTransportController.PauseCallCount);
        Assert.Equal(0, mediaTransportController.PlayCallCount);
        Assert.Equal(0, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task TrackChange_WhenFormatMustChange_AppliesItLiveWithoutTouchingTransport()
    {
        // The format is applied while the stream actively renders (muted): the media agent
        // rebuilds the invalidated stream on its own, so no pause/resume command is ever sent.
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0.01f,
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(LosslessFormat, finalStatus.AppliedFormat);
        // Exactly one muted pause/play after the rebuild: it realigns the audio with the
        // timeline so the rebuild duration is not cut off the END of the track.
        Assert.Equal(1, mediaTransportController.PauseCallCount);
        Assert.Equal(1, mediaTransportController.PlayCallCount);
        // No automatic track restart: a second transition right after the switch is the state a
        // quick user skip turns into a wedged renderer (proven live).
        Assert.Equal(0, mediaTransportController.SkipPreviousCallCount);
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task TrackChange_WhenLocalFileResultsInFormatChange_AppliesItLikeCatalogManifest()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0.01f,
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat, AudioFormatSource.LocalFile)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(LosslessFormat, finalStatus.AppliedFormat);
        Assert.Equal(1, mediaTransportController.PauseCallCount); // schedule realign
        Assert.Equal(1, mediaTransportController.PlayCallCount);
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task TrackChange_WhenUserPausesDuringRebuild_LeavesPlaybackAlone()
    {
        // The user pauses while the rebuilt stream is still coming up: the coordinator must not
        // run recovery (which would override the pause) and must not restart the track.
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f, // no audio (paused)
        };
        var applied = false;
        audioEndpointController.ApplyInvoked = () =>
        {
            applied = true;
            mediaTransportController.IsPlaying = false; // user pauses right at the switch
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.True(applied);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(0, mediaTransportController.PlayCallCount); // the user's pause is respected
        Assert.Equal(0, mediaTransportController.SkipPreviousCallCount); // no restart while paused
    }

    [Fact]
    public async Task TierFallback_WhenDeviceDiffers_AppliesTheTierFormatLikeAnyResolution()
    {
        // The tier value derives from the user's Apple Music quality setting; applying it keeps
        // the resolved and applied formats matched instead of leaving the device wherever the
        // previous track happened to put it.
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, FallbackFormat])
        {
            MasterPeakValue = 0.01f,
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(FallbackFormat, AudioFormatSource.TierFallback)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(FallbackFormat, finalStatus.AppliedFormat); // resolved == applied
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
        Assert.Equal(1, mediaTransportController.PauseCallCount); // schedule realign
        Assert.Equal(1, mediaTransportController.PlayCallCount);
    }

    [Fact]
    public async Task TierFallback_WhenTierAlreadyActive_DoesNotTouchTransportOrDevice()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, FallbackFormat, [CurrentFormat, FallbackFormat]);
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(FallbackFormat, AudioFormatSource.TierFallback)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.Equal("Resolver: TierFallback", finalStatus.ResolverStatusText);
        Assert.False(finalStatus.WasFormatChanged);
        Assert.Equal(FallbackFormat, finalStatus.AppliedFormat);
        Assert.Equal(0, mediaTransportController.PauseCallCount);
        Assert.Equal(0, mediaTransportController.PlayCallCount);
        Assert.Equal(0, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task TrackChange_WhenRestoreObservationFails_RecoversAfterOneNudge()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        // Endpoint stays silent after resume (zombie playback: status Playing, no audio,
        // timeline frozen) until the nudge's play lands, when audio starts flowing.
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
        };
        mediaTransportController.PlayInvoked = () =>
        {
            if (mediaTransportController.PlayCallCount >= 2)
            {
                audioEndpointController.MasterPeakValue = 0.01f;
            }
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        // The initial restore observation runs its full 4 s window before the nudge fires.
        var finalStatus = await finalStatusTask.WaitAsync(RecoveryTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(LosslessFormat, finalStatus.AppliedFormat);
        Assert.Equal(2, mediaTransportController.PauseCallCount);
        Assert.Equal(2, mediaTransportController.PlayCallCount);
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task TrackChange_WhenRecoveryNudgesExhausted_ReportsUnconfirmedRestart()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        // Endpoint never produces audio again — every nudge fails.
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not confirm audio") == true);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(RecoveryTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(LosslessFormat, finalStatus.AppliedFormat);
        Assert.Equal(2, mediaTransportController.PauseCallCount); // one per nudge
        Assert.Equal(2, mediaTransportController.PlayCallCount); // one per nudge
        Assert.Equal(1, mediaTransportController.ChangePlaybackPositionCallCount); // final-nudge seek escalation
        Assert.Equal(1, mediaTransportController.SkipNextCallCount); // hard wedge → skip-next escalation
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task OtherAppsAudio_NeverConfirmsDeadPlaybackAsRestored()
    {
        // Regression for a live-proven false positive: another application playing audio on the
        // same device made the endpoint MASTER meter read a peak while Apple Music's renderer
        // was dead, so every recovery "confirmed" restore and the zombie persisted forever.
        // All confirmations must read the media agent's own session peak instead.
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0.5f,            // another app is loud on the device…
            ProcessSessionPeakOverride = 0f,   // …but Apple Music renders NOTHING
            RenderSessionActiveProvider = () => true,
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not confirm audio") == true);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        // The rebuild observation and every recovery nudge must FAIL to confirm (the agent
        // produces no audio), ending in the skip-next escalation — never a false "restored".
        var finalStatus = await finalStatusTask.WaitAsync(RecoveryTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
        Assert.Equal(1, mediaTransportController.SkipNextCallCount); // escalated, not falsely recovered
        Assert.Equal(0, mediaTransportController.SkipPreviousCallCount); // no track restart on unconfirmed audio
    }

    [Fact]
    public async Task TrackChange_WhenNotPlayingAndEndpointSilent_SwitchesWithoutPauseOrResume()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController
        {
            IsPlaying = false,
        };
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.WasFormatChanged);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(LosslessFormat, finalStatus.AppliedFormat);
        Assert.Equal(0, mediaTransportController.PauseCallCount);
        Assert.Equal(0, mediaTransportController.PlayCallCount);
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task TrackChange_WhenNotPlayingButPeakShowsAudio_SwitchesLiveWithoutPause()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController
        {
            IsPlaying = false, // stale transport state: GSMTC says not playing…
        };
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            // …but the endpoint shows audio flowing: the stream is live, so the live-switch path
            // applies the format under it and waits for the agent's self-rebuild.
            MasterPeakValue = 0.05f,
            RenderSessionActiveProvider = () => true,
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(LosslessFormat, finalStatus.AppliedFormat);
        Assert.Equal(0, mediaTransportController.PauseCallCount);
        Assert.Equal(0, mediaTransportController.PlayCallCount);
        Assert.Equal(0, mediaTransportController.SkipPreviousCallCount); // transport not playing — no restart
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task UserPausesDuringResolution_SwitchesSilentlyAndDoesNotAutoResume()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
        };
        // The user pauses while the resolver is running. The coordinator must still apply the
        // format (silent switch) but must NOT play over the user's pause.
        var resolver = new DelegateResolver((_, _) =>
        {
            mediaTransportController.IsPlaying = false;
            return Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat));
        });
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.WasFormatChanged);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.True(finalStatus.WasFormatChanged);
        Assert.Equal(LosslessFormat, finalStatus.AppliedFormat);
        Assert.Equal(1, audioEndpointController.TryApplyFormatCallCount);
        Assert.Equal(0, mediaTransportController.PauseCallCount);
        Assert.Equal(0, mediaTransportController.PlayCallCount); // user's pause is respected
        Assert.False(mediaTransportController.IsPlaying);
    }

    [Fact]
    public async Task TrackChange_WhenRenderStreamNeverActivates_SkipsSwitchToAvoidZombie()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            // Transport says playing, but the media agent never starts rendering — the wedged
            // state where applying a format change would zombie playback.
            RenderSessionActiveProvider = () => false,
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("Render stream not active") == true);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        // The activation gate runs its full 4 s window, then the health check confirms the
        // stall and runs the recovery nudges (which cannot succeed here — the stream never
        // comes back) before the final status is published.
        var finalStatus = await finalStatusTask.WaitAsync(RecoveryTimeout);
        Assert.False(finalStatus.WasFormatChanged);
        Assert.Equal(0, audioEndpointController.TryApplyFormatCallCount); // never switched into the bad state
        Assert.Equal(1, mediaTransportController.PauseCallCount); // single no-switch recovery nudge
        Assert.Equal(1, mediaTransportController.PlayCallCount);
        Assert.Equal(1, mediaTransportController.SkipNextCallCount); // hard wedge → skip-next escalation
        Assert.Contains("could not be recovered", finalStatus.FailureReason);
    }

    [Fact]
    public async Task AlreadyActiveTrack_WhenPlayingWithNoRenderStream_RecoversWithNudges()
    {
        // The rapid-skip zombie: Apple Music wedges on its own (no switch involved) — SMTC
        // reports Playing but the media agent never starts a render stream. The no-switch
        // health check must detect the stall and revive playback with the recovery nudges.
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
        };
        var streamAlive = false;
        audioEndpointController.RenderSessionActiveProvider = () => streamAlive;
        mediaTransportController.PlayInvoked = () =>
        {
            streamAlive = true; // the nudge play revives the render stream
            audioEndpointController.MasterPeakValue = 0.01f;
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("recovered automatically") == true);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(RecoveryTimeout);
        Assert.Contains("Target format already active.", finalStatus.FailureReason);
        Assert.Equal(0, audioEndpointController.TryApplyFormatCallCount); // no switch was needed
        Assert.Equal(1, mediaTransportController.PauseCallCount); // one nudge revived playback
        Assert.Equal(1, mediaTransportController.PlayCallCount);
        Assert.Equal(0, mediaTransportController.SkipNextCallCount); // no escalation when nudges succeed
        Assert.True(mediaTransportController.IsPlaying);
    }

    [Fact]
    public async Task Watchdog_DetectsMidTrackStall_AndRecovers()
    {
        // A track passes its processing-time health check, then Apple Music wedges LATER,
        // mid-track (proven live: dead 0:00 playback minutes after a clean track change).
        // The continuous watchdog must notice and revive playback with no track change at all.
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
        };
        var streamAlive = true;
        audioEndpointController.RenderSessionActiveProvider = () => streamAlive && mediaTransportController.IsPlaying;
        mediaTransportController.PlayInvoked = () =>
        {
            streamAlive = true; // the nudge play revives the render stream
            audioEndpointController.MasterPeakValue = 0.01f;
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var processedTask = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());
        await processedTask.WaitAsync(DefaultTimeout);
        Assert.Equal(0, mediaTransportController.PauseCallCount); // processed healthy, untouched

        // Mid-track wedge strikes AFTER processing finished.
        var recoveringTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Recovering playback");
        streamAlive = false;

        await recoveringTask.WaitAsync(RecoveryTimeout); // watchdog noticed the stall on its own

        // Give the nudge a moment to complete, then verify playback was revived.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && (!streamAlive || !mediaTransportController.IsPlaying))
        {
            await Task.Delay(200);
        }

        Assert.True(streamAlive);
        Assert.True(mediaTransportController.IsPlaying);
        Assert.Equal(1, mediaTransportController.PauseCallCount); // one watchdog nudge
        Assert.Equal(1, mediaTransportController.PlayCallCount);
        Assert.Equal(0, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task RecoverySkipEscalation_FiresOnceThenHonorsCooldown()
    {
        // Two hard-wedged tracks in a row: the first failed recovery escalates to a skip-next;
        // the second, inside the cooldown window, must NOT skip again (no runaway skipping).
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
            RenderSessionActiveProvider = () => false, // renderer never comes up
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var firstFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());
        await firstFailure.WaitAsync(RecoveryTimeout);
        Assert.Equal(1, mediaTransportController.SkipNextCallCount);

        var secondFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);
        trackSource.RaiseTrackChanged(CreateTrack("Second Track"));
        await secondFailure.WaitAsync(RecoveryTimeout);

        Assert.Equal(1, mediaTransportController.SkipNextCallCount); // cooldown blocked a second skip
    }

    [Fact]
    public async Task CascadeOfRecoveryFailures_RestartsAppleMusic_AndRecovers()
    {
        // Three failed recoveries in a short window = the degraded-renderer cascade, where nudges
        // never work and skipping just moves the wedge forward. The coordinator must restart
        // Apple Music (the only known cure), confirm audio, and reset its cascade tracking.
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
        };
        var sessionAlive = false;
        var reviveOnNextPlay = false;
        audioEndpointController.RenderSessionActiveProvider = () => sessionAlive;
        var processController = new TestAppleMusicProcessController
        {
            RestartInvoked = () => reviveOnNextPlay = true, // a fresh Apple Music renders again
        };
        mediaTransportController.PlayInvoked = () =>
        {
            if (reviveOnNextPlay)
            {
                reviveOnNextPlay = false;
                sessionAlive = true;
                audioEndpointController.MasterPeakValue = 0.01f;
            }
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver, processController);
        var firstFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        // Wedge 1: nudge fails -> failure #1 -> below the cascade threshold -> skip-next.
        trackSource.RaiseTrackChanged(CreateTrack());
        await firstFailure.WaitAsync(RecoveryTimeout);
        Assert.Equal(0, processController.RestartCallCount);
        Assert.Equal(1, mediaTransportController.SkipNextCallCount);

        // Wedge 2: failure #2 — still below the threshold; the skip cooldown blocks another skip.
        var secondFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);
        trackSource.RaiseTrackChanged(CreateTrack("Second Track"));
        await secondFailure.WaitAsync(RecoveryTimeout);
        Assert.Equal(0, processController.RestartCallCount);

        // Wedge 3: failure #3 within the window -> cascade -> restart -> playback recovers.
        var recovered = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("was recovered automatically") == true);
        trackSource.RaiseTrackChanged(CreateTrack("Third Track"));
        await recovered.WaitAsync(RecoveryTimeout);
        Assert.Equal(1, processController.RestartCallCount);
        Assert.True(sessionAlive);

        // Wedge 4: the restart cleared the cascade state and its cooldown is active, so this
        // failure is #1 again -> no second restart.
        sessionAlive = false;
        audioEndpointController.MasterPeakValue = 0f;
        var fourthFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);
        trackSource.RaiseTrackChanged(CreateTrack("Fourth Track"));
        await fourthFailure.WaitAsync(RecoveryTimeout);
        Assert.Equal(1, processController.RestartCallCount);
    }

    [Fact]
    public async Task RestartEscalation_WhenDisabledInSettings_NeverRestarts()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
            RenderSessionActiveProvider = () => false, // permanent wedge
        };
        var processController = new TestAppleMusicProcessController();
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver, processController);
        var firstFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);

        var settings = CreateSettings();
        settings.RestartAppleMusicOnPlaybackFailure = false;
        await coordinator.StartAsync(settings, CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());
        await firstFailure.WaitAsync(RecoveryTimeout);

        var secondFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);
        trackSource.RaiseTrackChanged(CreateTrack("Second Track"));
        await secondFailure.WaitAsync(RecoveryTimeout);

        var thirdFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);
        trackSource.RaiseTrackChanged(CreateTrack("Third Track"));
        await thirdFailure.WaitAsync(RecoveryTimeout);

        Assert.Equal(0, processController.RestartCallCount); // toggle off — never restart, even past the threshold
        Assert.True(mediaTransportController.SkipNextCallCount >= 1); // skip escalations still run, bounded by their cooldown
    }

    [Fact]
    public async Task RestartEscalation_WhenRestartFails_ReportsFailureWithoutSkipping()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
            RenderSessionActiveProvider = () => false, // permanent wedge
        };
        var processController = new TestAppleMusicProcessController { RestartResult = false };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver, processController);
        var firstFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());
        await firstFailure.WaitAsync(RecoveryTimeout);

        var secondFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);
        trackSource.RaiseTrackChanged(CreateTrack("Second Track"));
        await secondFailure.WaitAsync(RecoveryTimeout);

        var skipsBeforeRestart = mediaTransportController.SkipNextCallCount;
        var thirdFailure = WaitForStatusAsync(coordinator, status => status.FailureReason?.Contains("could not be recovered") == true);
        trackSource.RaiseTrackChanged(CreateTrack("Third Track"));
        await thirdFailure.WaitAsync(RecoveryTimeout);

        Assert.Equal(1, processController.RestartCallCount); // restart attempted on the third failure
        Assert.Equal(skipsBeforeRestart, mediaTransportController.SkipNextCallCount); // no skip stacked on the failed restart
    }

    [Fact]
    public async Task AlreadyActiveTrack_WhenRenderStreamHealthy_DoesNotRunRecovery()
    {
        // Quiet-but-healthy playback keeps an ACTIVE render session, so the health check must
        // exit immediately without touching the transport (no false-positive nudges).
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f, // silent intro — but the stream is running
        };
        var resolver = new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var finalStatusTask = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());

        var finalStatus = await finalStatusTask.WaitAsync(DefaultTimeout);
        Assert.Equal(0, mediaTransportController.PauseCallCount);
        Assert.Equal(0, mediaTransportController.PlayCallCount);
        Assert.Equal(0, audioEndpointController.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task SecondTrack_MutesDeviceDuringSwitchAndUnmutesBeforeResume()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0.01f, // master peak reads pre-mute, so audio confirms even muted
        };
        // Alternate between formats so both tracks trigger a switch.
        var resolver = new DelegateResolver((track, _) => Task.FromResult<ResolvedAudioFormat?>(
            CreateResolvedFormat(track.Title == "Track" ? LosslessFormat : CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var firstRestoredTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());
        await firstRestoredTask.WaitAsync(DefaultTimeout);

        // No mute on the very first track: the coordinator has no cached target device yet.
        Assert.Empty(audioEndpointController.MuteCalls);

        var secondRestoredTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");
        trackSource.RaiseTrackChanged(CreateTrack("Second Track"));

        await secondRestoredTask.WaitAsync(DefaultTimeout);
        // The second track is muted instantly on track change and unmuted when processing ends.
        Assert.Equal([true, false], audioEndpointController.MuteCalls);
        Assert.False(audioEndpointController.MasterMute);
    }

    [Fact]
    public async Task SecondTrack_WhenDeviceMutedByUser_DoesNotTouchMute()
    {
        var trackSource = new TestTrackSource();
        var mediaTransportController = new TestMediaTransportController();
        var audioEndpointController = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0.01f, // master peak reads pre-mute, so audio confirms even muted
            MasterMute = true,
        };
        var resolver = new DelegateResolver((track, _) => Task.FromResult<ResolvedAudioFormat?>(
            CreateResolvedFormat(track.Title == "Track" ? LosslessFormat : CurrentFormat)));
        await using var coordinator = CreateCoordinator(trackSource, mediaTransportController, audioEndpointController, resolver);
        var firstRestoredTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);

        trackSource.RaiseTrackChanged(CreateTrack());
        await firstRestoredTask.WaitAsync(DefaultTimeout);

        var secondRestoredTask = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");
        trackSource.RaiseTrackChanged(CreateTrack("Second Track"));

        await secondRestoredTask.WaitAsync(DefaultTimeout);
        // The user muted the device themselves; the coordinator must never toggle it.
        Assert.Empty(audioEndpointController.MuteCalls);
        Assert.True(audioEndpointController.MasterMute);
    }

    private static SwitchingCoordinator CreateCoordinator(
        TestTrackSource trackSource,
        TestMediaTransportController mediaTransportController,
        TestAudioEndpointController audioEndpointController,
        IFormatResolver resolver,
        TestAppleMusicProcessController? appleMusicProcessController = null)
    {
        // Mirror reality by default: the media agent's render session is active while playing,
        // and keeps draining for a moment after a pause before going inactive.
        DateTimeOffset? inactiveSince = null;
        audioEndpointController.RenderSessionActiveProvider ??= () =>
        {
            if (mediaTransportController.IsPlaying)
            {
                inactiveSince = null;
                return true;
            }

            inactiveSince ??= DateTimeOffset.UtcNow;
            return DateTimeOffset.UtcNow - inactiveSince < TimeSpan.FromMilliseconds(2200);
        };
        var logger = new DiagnosticsLogger(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        return new SwitchingCoordinator(
            trackSource,
            mediaTransportController,
            new ResolverChain([resolver]),
            audioEndpointController,
            logger,
            appleMusicProcessController);
    }

    private static AppSettings CreateSettings(AudioFormatCandidate? originalTargetFormat = null) =>
        new()
        {
            DeviceSelectionMode = DeviceSelectionMode.FollowDefault,
            SwitchBitDepth = true,
            OriginalTarget = originalTargetFormat is null
                ? null
                : new OriginalTargetSnapshot(
                    DefaultDevice.Id,
                    DefaultDevice.FriendlyName,
                    originalTargetFormat.SampleRateHz,
                    originalTargetFormat.BitDepth,
                    originalTargetFormat.Channels,
                    DateTimeOffset.UtcNow),
        };

    private static TrackSnapshot CreateTrack(string title = "Track") =>
        new(
            "AppleMusic",
            null,
            title,
            "Artist",
            "Album",
            "test",
            DateTimeOffset.UtcNow);

    private static ResolvedAudioFormat CreateResolvedFormat(
        AudioFormatCandidate format,
        AudioFormatSource source = AudioFormatSource.CatalogManifest) =>
        new(
            format.SampleRateHz,
            format.BitDepth,
            ResolutionConfidence.Exact,
            source,
            $"{source}: {format.DisplayName}");

    private static Task<SwitchingStatus> WaitForStatusAsync(
        SwitchingCoordinator coordinator,
        Func<SwitchingStatus, bool> predicate)
    {
        var completionSource = new TaskCompletionSource<SwitchingStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SwitchingStatus>? handler = null;
        handler = (_, status) =>
        {
            if (!predicate(status))
            {
                return;
            }

            coordinator.StatusChanged -= handler;
            completionSource.TrySetResult(status);
        };

        coordinator.StatusChanged += handler;
        return completionSource.Task;
    }

    private sealed class TestAppleMusicProcessController : IAppleMusicProcessController
    {
        public int RestartCallCount { get; private set; }

        public bool RestartResult { get; set; } = true;

        public Action? RestartInvoked { get; set; }

        public Task<bool> TryRestartAsync(CancellationToken cancellationToken)
        {
            RestartCallCount++;
            RestartInvoked?.Invoke();
            return Task.FromResult(RestartResult);
        }
    }

    private sealed class TestTrackSource : ITrackSource
    {
        public event EventHandler<TrackSnapshot>? TrackChanged;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseTrackChanged(TrackSnapshot track) => TrackChanged?.Invoke(this, track);
    }

    private sealed class TestMediaTransportController : IMediaTransportController
    {
        public int PauseCallCount { get; private set; }

        public int PlayCallCount { get; private set; }

        public int TogglePlayPauseCallCount { get; private set; }

        public bool IsPlaying { get; set; } = true;

        public bool CanPause { get; set; } = true;

        public bool CanPlay { get; set; } = true;

        public bool PauseResult { get; set; } = true;

        public bool PlayResult { get; set; } = true;

        public bool TogglePlayPauseResult { get; set; }

        public int PlayFailuresBeforeSuccess { get; set; }

        public Action? PlayInvoked { get; set; }

        public Action? PauseInvoked { get; set; }

        public TimeSpan? TimelinePosition { get; set; } = TimeSpan.FromSeconds(12);

        public Queue<MediaTransportPlaybackStatus> PlaybackStatusSequence { get; } = new();

        public int GetPlaybackStateCallCount { get; private set; }

        public MediaTransportState GetPlaybackState()
        {
            GetPlaybackStateCallCount++;
            var status = PlaybackStatusSequence.Count switch
            {
                0 => IsPlaying ? MediaTransportPlaybackStatus.Playing : MediaTransportPlaybackStatus.Paused,
                1 => PlaybackStatusSequence.Peek(),
                _ => PlaybackStatusSequence.Dequeue(),
            };

            return new(
                status,
                CanPause,
                CanPlay,
                TimelinePosition,
                DateTimeOffset.UtcNow);
        }

        public Task<bool> TryPauseAsync(CancellationToken cancellationToken)
        {
            PauseCallCount++;
            PauseInvoked?.Invoke();
            if (PauseResult)
            {
                IsPlaying = false;
            }

            return Task.FromResult(PauseResult);
        }

        public Task<bool> TryPlayAsync(CancellationToken cancellationToken)
        {
            PlayCallCount++;
            PlayInvoked?.Invoke();
            if (PlayFailuresBeforeSuccess > 0)
            {
                PlayFailuresBeforeSuccess--;
                return Task.FromResult(false);
            }

            if (PlayResult)
            {
                IsPlaying = true;
            }

            return Task.FromResult(PlayResult);
        }


        public Task<bool> TryTogglePlayPauseAsync(CancellationToken cancellationToken)
        {
            TogglePlayPauseCallCount++;
            if (TogglePlayPauseResult)
            {
                IsPlaying = !IsPlaying;
            }

            return Task.FromResult(TogglePlayPauseResult);
        }

        public int ChangePlaybackPositionCallCount { get; private set; }

        public Task<bool> TryChangePlaybackPositionAsync(TimeSpan position, CancellationToken cancellationToken)
        {
            ChangePlaybackPositionCallCount++;
            return Task.FromResult(true);
        }

        public int SkipPreviousCallCount { get; private set; }

        public bool SkipPreviousResult { get; set; } = true;

        public Task<bool> TrySkipPreviousAsync(CancellationToken cancellationToken)
        {
            SkipPreviousCallCount++;
            return Task.FromResult(SkipPreviousResult);
        }

        public int SkipNextCallCount { get; private set; }

        public Task<bool> TrySkipNextAsync(CancellationToken cancellationToken)
        {
            SkipNextCallCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class TestAudioEndpointController : IAudioEndpointController
    {
        private readonly AudioDeviceInfo _defaultDevice;
        private readonly IReadOnlyList<AudioFormatCandidate> _supportedFormats;

        public TestAudioEndpointController(
            AudioDeviceInfo defaultDevice,
            AudioFormatCandidate currentDeviceFormat,
            IReadOnlyList<AudioFormatCandidate> supportedFormats)
        {
            _defaultDevice = defaultDevice;
            CurrentDeviceFormat = currentDeviceFormat;
            CurrentMixFormat = currentDeviceFormat;
            _supportedFormats = supportedFormats;
        }

        public int TryApplyFormatCallCount { get; private set; }

        public AudioFormatCandidate? CurrentDeviceFormat { get; private set; }

        public AudioFormatCandidate? CurrentMixFormat { get; private set; }

        public float? MasterPeakValue { get; set; }

        public Queue<float?> MasterPeakSequence { get; } = new();

        public int GetMasterPeakValueCallCount { get; private set; }

        public bool? MasterMute { get; set; } = false;

        public List<bool> MuteCalls { get; } = new();

        public Func<bool?>? RenderSessionActiveProvider { get; set; }

        public Queue<bool?> RenderSessionActiveSequence { get; } = new();

        public int IsProcessSessionActiveCallCount { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => [_defaultDevice];

        public AudioDeviceInfo? GetDefaultRenderDevice() => _defaultDevice;

        public AudioFormatCandidate? GetCurrentDeviceFormat(string deviceId) => CurrentDeviceFormat;

        public AudioFormatCandidate? GetCurrentMixFormat(string deviceId) => CurrentMixFormat;

        public IReadOnlyList<AudioFormatCandidate> GetSupportedFormats(string deviceId, bool forceRefresh = false) => _supportedFormats;

        public string? DescribeSupportedFormat(string deviceId, AudioFormatCandidate format) => "shared-mode format";

        public string? GetLastApplyDiagnostics(string deviceId) => null;

        public string? GetLastProbeDiagnostics(string deviceId) => null;

        public float? GetMasterPeakValue(string deviceId)
        {
            GetMasterPeakValueCallCount++;
            return ReadPeak();
        }

        // When set, the per-process session peak diverges from the master peak — models other
        // applications playing audio on the device while Apple Music renders nothing.
        public float? ProcessSessionPeakOverride { get; set; }

        // In these tests the modeled peak IS Apple's audio by default, so the per-process
        // session peak reads the same source. (The production implementations differ: the
        // session peak excludes other applications' audio.)
        public float? GetProcessSessionPeak(string deviceId, string processName)
        {
            GetMasterPeakValueCallCount++;
            return ProcessSessionPeakOverride ?? ReadPeak();
        }

        private float? ReadPeak() =>
            MasterPeakSequence.Count switch
            {
                0 => MasterPeakValue,
                1 => MasterPeakSequence.Peek(),
                _ => MasterPeakSequence.Dequeue(),
            };

        public bool? IsProcessSessionActive(string deviceId, string processName)
        {
            IsProcessSessionActiveCallCount++;
            if (RenderSessionActiveSequence.Count > 0)
            {
                return RenderSessionActiveSequence.Count == 1
                    ? RenderSessionActiveSequence.Peek()
                    : RenderSessionActiveSequence.Dequeue();
            }

            return RenderSessionActiveProvider?.Invoke() ?? false;
        }

        public bool? GetMasterMute(string deviceId) => MasterMute;

        public bool TrySetMasterMute(string deviceId, bool muted)
        {
            MuteCalls.Add(muted);
            MasterMute = muted;
            return true;
        }

        public Action? ApplyInvoked { get; set; }

        public bool TryApplyFormat(
            string deviceId,
            AudioFormatCandidate format,
            out AudioFormatCandidate? verifiedDeviceFormat,
            out string failureReason)
        {
            TryApplyFormatCallCount++;
            ApplyInvoked?.Invoke();
            CurrentDeviceFormat = format;
            CurrentMixFormat = format;
            verifiedDeviceFormat = format;
            failureReason = string.Empty;
            return true;
        }
    }

    private sealed class DelegateResolver(Func<TrackSnapshot, CancellationToken, Task<ResolvedAudioFormat?>> resolveAsync) : IFormatResolver
    {
        public string Name => nameof(DelegateResolver);

        public Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken) =>
            resolveAsync(track, cancellationToken);
    }

    private sealed class BlockingResolver(TestMediaTransportController mediaTransportController) : IFormatResolver
    {
        private readonly TaskCompletionSource<object?> _resolveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ResolvedAudioFormat?> _resultSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => nameof(BlockingResolver);

        public Task ResolveStarted => _resolveStarted.Task;

        public int PauseCallCountWhenResolveStarted { get; private set; }

        public async Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
        {
            PauseCallCountWhenResolveStarted = mediaTransportController.PauseCallCount;
            _resolveStarted.TrySetResult(null);
            return await _resultSource.Task.WaitAsync(cancellationToken);
        }

        public void Release(ResolvedAudioFormat? result) => _resultSource.TrySetResult(result);
    }
}
