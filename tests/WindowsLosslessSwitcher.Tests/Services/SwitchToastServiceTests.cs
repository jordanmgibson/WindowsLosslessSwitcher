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

        service.ShowSwitchedFormat("DAC", PreviousFormat, null);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null);

        var switchToast = Assert.Single(factory.Toasts);
        Assert.Equal(0, switchToast.CloseCount);

        switchToast.RaiseClosed();

        Assert.Equal(2, factory.Toasts.Count);
        Assert.Equal("Format updated for next playback", factory.Toasts[1].Title);
        Assert.True(factory.Toasts[1].WasShown);
    }

    [Fact]
    public void CacheUpdates_AreDeferredInFifoOrder()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);
        var secondUpdatedFormat = new AudioFormatCandidate(192000, 24, 2);

        service.ShowSwitchedFormat("DAC", PreviousFormat, null);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null);
        service.ShowFormatCacheUpdated("DAC", UpdatedFormat, secondUpdatedFormat, null);

        factory.Toasts[0].RaiseClosed();
        Assert.Contains("96 kHz", factory.Toasts[1].Message);

        factory.Toasts[1].RaiseClosed();
        Assert.Contains("192 kHz", factory.Toasts[2].Message);
    }

    [Fact]
    public void ReplacingSwitchToast_DoesNotDrainQueuedCacheUpdateDuringSynchronousClose()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);

        service.ShowSwitchedFormat("DAC", PreviousFormat, null);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null);
        service.ShowSwitchedFormat("DAC", UpdatedFormat, null);

        Assert.Equal(2, factory.Toasts.Count);
        Assert.Equal(1, factory.Toasts[0].CloseCount);
        Assert.Equal("Switched audio format", factory.Toasts[1].Title);

        factory.Toasts[1].RaiseClosed();
        Assert.Equal("Format updated for next playback", factory.Toasts[2].Title);
    }

    [Fact]
    public void Dispose_ClosesCurrentToastAndDropsPendingUpdates()
    {
        var factory = new RecordingToastFactory();
        var service = CreateService(factory);
        service.ShowSwitchedFormat("DAC", PreviousFormat, null);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null);

        service.Dispose();

        Assert.Single(factory.Toasts);
        Assert.Equal(1, factory.Toasts[0].CloseCount);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null);
        Assert.Single(factory.Toasts);
    }

    [Fact]
    public void DiscardPendingFormatCacheUpdates_PreventsDeferredToast()
    {
        var factory = new RecordingToastFactory();
        using var service = CreateService(factory);
        service.ShowSwitchedFormat("DAC", PreviousFormat, null);
        service.ShowFormatCacheUpdated("DAC", PreviousFormat, UpdatedFormat, null);

        service.DiscardPendingFormatCacheUpdates();
        factory.Toasts[0].RaiseClosed();

        Assert.Single(factory.Toasts);
    }

    private static SwitchToastService CreateService(RecordingToastFactory factory) =>
        new(factory.Create, () => true, action => action());

    private sealed class RecordingToastFactory
    {
        public List<RecordingToast> Toasts { get; } = [];

        public ISwitchToastWindow Create(string title, string message, string? deviceName, string? trackDetails)
        {
            var toast = new RecordingToast(title, message);
            Toasts.Add(toast);
            return toast;
        }
    }

    private sealed class RecordingToast(string title, string message) : ISwitchToastWindow
    {
        public event EventHandler? Closed;

        public string Title { get; } = title;

        public string Message { get; } = message;

        public bool WasShown { get; private set; }

        public int CloseCount { get; private set; }

        public void Show() => WasShown = true;

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
