using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class TrackRestartControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedOrNoOpSeek_FallsBackOnceAndConfirmsSameTrack(bool canSeek)
    {
        var snapshot = Snapshot(7, canSeek);
        var seeks = 0;
        var previous = 0;
        var result = await TrackRestartController.TryRestartAsync("song", _ => Task.FromResult(snapshot),
            _ => { seeks++; return Task.FromResult(true); },
            _ => { previous++; snapshot = snapshot with { Position = TimeSpan.Zero }; return Task.FromResult(true); },
            CancellationToken.None, TimeSpan.Zero);
        Assert.True(result);
        Assert.Equal(canSeek ? 1 : 0, seeks);
        Assert.Equal(1, previous);
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(2)]
    [InlineData(4.99)]
    public async Task NearBeginning_DoesNotRiskSkippingToPreviousRecording(double seconds)
    {
        var previous = 0;
        var result = await TrackRestartController.TryRestartAsync("song", _ => Task.FromResult(Snapshot(seconds)),
            _ => Task.FromResult(false), _ => { previous++; return Task.FromResult(true); }, CancellationToken.None);
        Assert.False(result);
        Assert.Equal(0, previous);
    }

    [Fact]
    public async Task ChangedRecordingDuringNoOpSeek_DoesNotSendPrevious()
    {
        var snapshot = Snapshot(7, true);
        var previous = 0;
        var result = await TrackRestartController.TryRestartAsync("song", _ => Task.FromResult(snapshot),
            _ => { snapshot = snapshot with { TrackKey = "next-song" }; return Task.FromResult(true); },
            _ => { previous++; return Task.FromResult(true); }, CancellationToken.None);
        Assert.False(result);
        Assert.Equal(0, previous);
    }

    [Fact]
    public async Task AcknowledgedPreviousWithoutTimelineReset_IsNotSuccess()
    {
        var result = await TrackRestartController.TryRestartAsync("song", _ => Task.FromResult(Snapshot(7)),
            _ => Task.FromResult(false), _ => Task.FromResult(true), CancellationToken.None, TimeSpan.Zero);
        Assert.False(result);
    }

    [Fact]
    public async Task RealSeek_DoesNotAlsoSendPrevious()
    {
        var snapshot = Snapshot(7, true);
        var previous = 0;
        var result = await TrackRestartController.TryRestartAsync("song", _ => Task.FromResult(snapshot),
            _ => { snapshot = snapshot with { Position = TimeSpan.Zero }; return Task.FromResult(true); },
            _ => { previous++; return Task.FromResult(true); }, CancellationToken.None);
        Assert.True(result);
        Assert.Equal(0, previous);
    }

    [Fact]
    public async Task CancelledAfterRead_SendsNoCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TrackRestartController.TryRestartAsync("song",
            _ => { cancellation.Cancel(); return Task.FromResult(Snapshot(7, true)); },
            _ => { commands++; return Task.FromResult(true); },
            _ => { commands++; return Task.FromResult(true); }, cancellation.Token));
        Assert.Equal(0, commands);
    }

    [Theory]
    [InlineData(false, "song", 7)]
    [InlineData(true, "other-song", 7)]
    [InlineData(true, "song", -1)]
    public async Task UntrustedState_SendsNoCommand(bool paused, string key, double position)
    {
        var commands = 0;
        var result = await TrackRestartController.TryRestartAsync("song",
            _ => Task.FromResult(Snapshot(position) with { IsPaused = paused, TrackKey = key }),
            _ => { commands++; return Task.FromResult(true); },
            _ => { commands++; return Task.FromResult(true); }, CancellationToken.None);
        Assert.False(result);
        Assert.Equal(0, commands);
    }

    private static RestartPlaybackSnapshot Snapshot(double seconds, bool canSeek = false) =>
        new("song", TimeSpan.FromSeconds(seconds), canSeek, true, true);
}
