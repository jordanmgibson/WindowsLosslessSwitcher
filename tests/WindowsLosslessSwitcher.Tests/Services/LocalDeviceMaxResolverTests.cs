using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class LocalDeviceMaxResolverTests
{
    private static readonly AudioFormatCandidate Rate44_16 = new(44100, 16, 2);
    private static readonly AudioFormatCandidate Rate96_24 = new(96000, 24, 2);
    private static readonly AudioFormatCandidate Rate192_24 = new(192000, 24, 2);

    [Fact]
    public async Task NoFileReader_ReturnsNullInsteadOfDeviceMax()
    {
        // Without file knowledge the resolver must NOT gamble on a device-max jump (the main
        // zombie trigger); returning null lets the tier fallback skip the switch safely.
        var endpoint = new StubEndpoint([Rate44_16, Rate96_24, Rate192_24]);
        var resolver = CreateResolver(endpoint);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task MaxDepthAtFileRate_IsSelectedForLossyFile()
    {
        var endpoint = new StubEndpoint([new(96000, 16, 2), Rate96_24]);
        var reader = new StubFileFormatReader(new LocalTrackFileFormat(96000, 0, "cache.mp3"));
        var resolver = CreateResolver(endpoint, reader);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(96000, result!.SampleRateHz);
        Assert.Equal(24, result.BitDepth);
    }

    [Fact]
    public async Task FileFormatKnown_UsesFileRateInsteadOfDeviceMax()
    {
        var endpoint = new StubEndpoint([Rate44_16, new(44100, 24, 2), Rate96_24, Rate192_24]);
        var reader = new StubFileFormatReader(new LocalTrackFileFormat(44100, 0, "cache.mp3"));
        var resolver = CreateResolver(endpoint, reader);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(44100, result!.SampleRateHz);
        // Lossy file reports no bit depth, so the max depth at the file's rate is applied.
        Assert.Equal(24, result.BitDepth);
        Assert.Equal(AudioFormatSource.LocalFile, result.Source);
        Assert.Equal(ResolutionConfidence.Exact, result.Confidence);
    }

    [Fact]
    public async Task FileBitDepthSupportedAtRate_IsMatchedExactly()
    {
        var endpoint = new StubEndpoint([Rate44_16, new(44100, 24, 2), Rate192_24]);
        var reader = new StubFileFormatReader(new LocalTrackFileFormat(44100, 16, "cache.m4a"));
        var resolver = CreateResolver(endpoint, reader);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(44100, result!.SampleRateHz);
        Assert.Equal(16, result.BitDepth);
    }

    [Fact]
    public async Task FileBitDepthUnsupportedAtRate_UsesMaxDepthAtFileRate()
    {
        var endpoint = new StubEndpoint([Rate96_24, Rate192_24]);
        var reader = new StubFileFormatReader(new LocalTrackFileFormat(96000, 32, "cache.m4a"));
        var resolver = CreateResolver(endpoint, reader);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(96000, result!.SampleRateHz);
        Assert.Equal(24, result.BitDepth);
    }

    [Fact]
    public async Task FileRateUnsupportedByDevice_ReturnsNull()
    {
        var endpoint = new StubEndpoint([Rate96_24, Rate192_24]);
        var reader = new StubFileFormatReader(new LocalTrackFileFormat(44100, 16, "cache.mp3"));
        var resolver = CreateResolver(endpoint, reader);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FileNotReadable_ReturnsNull()
    {
        var endpoint = new StubEndpoint([Rate44_16, Rate192_24]);
        var reader = new StubFileFormatReader(format: null);
        var resolver = CreateResolver(endpoint, reader);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FileReaderThrows_ReturnsNull()
    {
        var endpoint = new StubEndpoint([Rate44_16, Rate192_24]);
        var reader = new StubFileFormatReader(format: null, throwOnRead: true);
        var resolver = CreateResolver(endpoint, reader);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task NoDefaultDevice_ReturnsNull()
    {
        var endpoint = new StubEndpoint([Rate44_16], hasDefaultDevice: false);
        var resolver = CreateResolver(endpoint);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task NoSupportedFormats_ReturnsNull()
    {
        var endpoint = new StubEndpoint([]);
        var resolver = CreateResolver(endpoint);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.Null(result);
    }

    private static LocalDeviceMaxResolver CreateResolver(
        StubEndpoint endpoint,
        ILocalTrackFileFormatReader? fileFormatReader = null)
    {
        var logger = new DiagnosticsLogger(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        return new LocalDeviceMaxResolver(endpoint, fileFormatReader, logger);
    }

    private static TrackSnapshot CreateTrack() =>
        new(
            "AppleInc.AppleMusicWin_nzyj5cx40ttqa",
            null,
            "Track",
            "Artist",
            "Album",
            "test",
            DateTimeOffset.UtcNow);

    private sealed class StubFileFormatReader(LocalTrackFileFormat? format, bool throwOnRead = false)
        : ILocalTrackFileFormatReader
    {
        public LocalTrackFileFormat? TryReadFormat(TrackSnapshot track) =>
            throwOnRead ? throw new IOException("file locked") : format;
    }

    private sealed class StubEndpoint(IReadOnlyList<AudioFormatCandidate> supportedFormats, bool hasDefaultDevice = true)
        : IAudioEndpointController
    {
        private static readonly AudioDeviceInfo Device = new("device-id", "Test DAC", true);

        public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => hasDefaultDevice ? [Device] : [];

        public AudioDeviceInfo? GetDefaultRenderDevice() => hasDefaultDevice ? Device : null;

        public AudioFormatCandidate? GetCurrentDeviceFormat(string deviceId) => supportedFormats.Count == 0 ? null : supportedFormats[0];

        public AudioFormatCandidate? GetCurrentMixFormat(string deviceId) => GetCurrentDeviceFormat(deviceId);

        public IReadOnlyList<AudioFormatCandidate> GetSupportedFormats(string deviceId, bool forceRefresh = false) => supportedFormats;

        public string? DescribeSupportedFormat(string deviceId, AudioFormatCandidate format) => format.DisplayName;

        public string? GetLastApplyDiagnostics(string deviceId) => null;

        public string? GetLastProbeDiagnostics(string deviceId) => null;

        public float? GetMasterPeakValue(string deviceId) => null;

        public float? GetProcessSessionPeak(string deviceId, string processName) => null;

        public bool? GetMasterMute(string deviceId) => false;

        public bool? IsProcessSessionActive(string deviceId, string processName) => false;

        public bool TrySetMasterMute(string deviceId, bool muted) => true;

        public bool TryApplyFormat(string deviceId, AudioFormatCandidate format, out AudioFormatCandidate? verifiedDeviceFormat, out string failureReason)
        {
            verifiedDeviceFormat = format;
            failureReason = string.Empty;
            return true;
        }
    }
}
