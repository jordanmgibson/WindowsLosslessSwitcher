using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class SwitchToastServiceTests
{
    private static readonly AudioFormatCandidate PreviousFormat = new(44100, 16, 2);
    private static readonly AudioFormatCandidate UpdatedFormat = new(96000, 24, 2);

    [Fact]
    public void CacheUpdate_DoesNotCloseSwitchToastAndShowsAfterItCloses()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);

        service.ShowSwitchedFormat("DAC", null, PreviousFormat, null, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true);

        var switchToast = Assert.Single(factory.Toasts);
        Assert.Equal(0, switchToast.CloseCount);

        switchToast.RaiseClosed();

        Assert.Equal(2, factory.Toasts.Count);
        Assert.Equal("FORMAT UPDATED FOR NEXT PLAYBACK", factory.Toasts[1].Content.Kicker);
        Assert.True(factory.Toasts[1].WasShown);
    }

    [Fact]
    public void CacheUpdates_AreDeferredInFifoOrder()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);
        var secondUpdatedFormat = new AudioFormatCandidate(192000, 24, 2);

        service.ShowSwitchedFormat("DAC", null, PreviousFormat, null, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", UpdatedFormat, secondUpdatedFormat, null, null, includeMetadata: true);

        factory.Toasts[0].RaiseClosed();
        Assert.Contains("96 kHz", factory.Toasts[1].Content.NewFormatText);

        factory.Toasts[1].RaiseClosed();
        Assert.Contains("192 kHz", factory.Toasts[2].Content.NewFormatText);
    }

    [Fact]
    public void ReplacingSwitchToast_DoesNotDrainQueuedCacheUpdateDuringSynchronousClose()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);

        service.ShowSwitchedFormat("DAC", null, PreviousFormat, null, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true);
        service.ShowSwitchedFormat("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true);

        Assert.Equal(2, factory.Toasts.Count);
        Assert.Equal(1, factory.Toasts[0].CloseCount);
        Assert.Equal("LOSSLESS SWITCH", factory.Toasts[1].Content.Kicker);

        factory.Toasts[1].RaiseClosed();
        Assert.Equal("FORMAT UPDATED FOR NEXT PLAYBACK", factory.Toasts[2].Content.Kicker);
    }

    [Fact]
    public void Dispose_ClosesCurrentToastAndDropsPendingUpdates()
    {
        var factory = new RecordingToastFactory();
        var service = CreateService(factory);
        service.ShowSwitchedFormat("DAC", null, PreviousFormat, null, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true);

        service.Dispose();

        Assert.Single(factory.Toasts);
        Assert.Equal(1, factory.Toasts[0].CloseCount);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true);
        Assert.Single(factory.Toasts);
    }

    [Fact]
    public void PendingCacheUpdates_AreCappedToNewestThree()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);
        service.ShowSwitchedFormat("DAC", null, PreviousFormat, null, null, includeMetadata: true);
        for (var i = 1; i <= 4; i++)
        {
            service.ShowFormatCacheUpdated(
                "DAC",
                PreviousFormat,
                new AudioFormatCandidate(48000 * i, 24, 2),
                null,
                null,
                includeMetadata: true);
        }

        // Drain: the oldest queued update (48 kHz) was dropped; the newest three survive in order.
        factory.Toasts[0].RaiseClosed();
        Assert.Contains("96 kHz", factory.Toasts[1].Content.NewFormatText);
        factory.Toasts[1].RaiseClosed();
        Assert.Contains("144 kHz", factory.Toasts[2].Content.NewFormatText);
        factory.Toasts[2].RaiseClosed();
        Assert.Contains("192 kHz", factory.Toasts[3].Content.NewFormatText);
        factory.Toasts[3].RaiseClosed();
        Assert.Equal(4, factory.Toasts.Count);
    }

    [Fact]
    public void PendingCacheUpdates_CoalescePerTrackKeepingLatest()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);
        var track = CreateTrack("Song A");
        var otherTrack = CreateTrack("Song B");

        service.ShowSwitchedFormat("DAC", null, PreviousFormat, null, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, track, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, otherTrack, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", UpdatedFormat, new AudioFormatCandidate(192000, 24, 2), track, null, includeMetadata: true);

        // The stale update for the same track was replaced by the newer one; the other track's
        // update is untouched and keeps its queue position.
        factory.Toasts[0].RaiseClosed();
        Assert.Contains("Song B", factory.Toasts[1].Content.TrackLine ?? string.Empty);
        factory.Toasts[1].RaiseClosed();
        Assert.Contains("Song A", factory.Toasts[2].Content.TrackLine ?? string.Empty);
        Assert.Contains("192 kHz", factory.Toasts[2].Content.NewFormatText);
        factory.Toasts[2].RaiseClosed();
        Assert.Equal(3, factory.Toasts.Count);
    }

    [Fact]
    public void ShowFailure_DoesNotWedgeLaterCacheUpdates()
    {
        var factory = new RecordingToastFactory { ThrowOnNextShow = true };
        using var service = CreateService(factory);

        Assert.ThrowsAny<Exception>(() =>
            service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true));

        // The dead window is not left registered as current, so the next update still shows.
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true);
        Assert.Equal(2, factory.Toasts.Count);
        Assert.True(factory.Toasts[1].WasShown);
    }

    [Fact]
    public void DiscardPendingFormatCacheUpdates_PreventsDeferredToast()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);
        service.ShowSwitchedFormat("DAC", null, PreviousFormat, null, null, includeMetadata: true);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null, null, includeMetadata: true);

        service.DiscardPendingFormatCacheUpdates();
        factory.Toasts[0].RaiseClosed();

        Assert.Single(factory.Toasts);
    }

    [Fact]
    public void MetadataToggle_SelectsVariant()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);

        service.ShowSwitchedFormat("DAC", PreviousFormat, UpdatedFormat, CreateTrack("Song"), null, includeMetadata: true);
        Assert.Equal(ToastVariant.Rich, factory.Toasts[0].Content.Variant);
        Assert.Equal("16-bit / 44.1 kHz", factory.Toasts[0].Content.OldFormatText);
        Assert.Equal("Song — Artist", factory.Toasts[0].Content.TrackLine);

        service.ShowSwitchedFormat("DAC", PreviousFormat, UpdatedFormat, CreateTrack("Song"), null, includeMetadata: false);
        Assert.Equal(ToastVariant.Pill, factory.Toasts[1].Content.Variant);
        Assert.Equal("96 kHz", factory.Toasts[1].Content.NewRateText);
        Assert.Equal("24-bit", factory.Toasts[1].Content.NewBitsText);
        Assert.Null(factory.Toasts[1].Content.TrackLine);
    }

    [Fact]
    public void RateUndetermined_IsAlwaysRich()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);

        service.ShowRateUndetermined("DAC", UpdatedFormat, null);

        var toast = Assert.Single(factory.Toasts);
        Assert.Equal(ToastVariant.Rich, toast.Content.Variant);
        Assert.Equal("RATE UNDETERMINED", toast.Content.Kicker);
        Assert.Null(toast.Content.OldFormatText);
        Assert.Contains("96 kHz", toast.Content.NewFormatText);
    }

    private static SwitchToastService CreateService(RecordingToastFactory factory) =>
        new(factory.Create, () => true, action => action());

    private static TrackSnapshot CreateTrack(string title) =>
        new(
            "AppleMusic",
            null,
            title,
            "Artist",
            "Album",
            "test",
            DateTimeOffset.UtcNow);

    private sealed class RecordingToastFactory
    {
        public List<RecordingToast> Toasts { get; } = [];

        public bool ThrowOnNextShow { get; set; }

        public ISwitchToastWindow Create(SwitchToastContent content)
        {
            var toast = new RecordingToast(content, throwOnShow: ThrowOnNextShow);
            ThrowOnNextShow = false;
            Toasts.Add(toast);
            return toast;
        }
    }

    private sealed class RecordingToast(SwitchToastContent content, bool throwOnShow = false)
        : ISwitchToastWindow
    {
        public event EventHandler? Closed;

        public SwitchToastContent Content { get; } = content;

        public bool WasShown { get; private set; }

        public int CloseCount { get; private set; }

        public void Show()
        {
            if (throwOnShow)
            {
                throw new InvalidOperationException("Window creation failed.");
            }

            WasShown = true;
        }

        public void Close()
        {
            CloseCount++;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public void StartAutoClose()
        {
        }

        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }
}
