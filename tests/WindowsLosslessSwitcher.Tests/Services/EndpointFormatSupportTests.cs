using System.Buffers.Binary;
using NAudio.Wave;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class EndpointFormatSupportTests
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");

    [Fact]
    public void CreateProbeFormats_24Bit_IncludesPaddedContainerVariant()
    {
        // USB Audio Class 2 DACs (FiiO K11/KA11, JDS Atom DAC+) only accept 24-bit audio as
        // 24 valid bits in a 32-bit container; without this probe they appear 16-bit-only.
        var formats = EndpointFormatSupport.CreateProbeFormats(48000, 24, 2).ToList();

        var padded = formats.SingleOrDefault(format =>
            EndpointFormatSupport.CreateDescriptor(format, EndpointFormatOrigin.ExclusiveProbe).Kind ==
            EndpointFormatKind.WaveFormatExtensiblePadded);

        Assert.NotNull(padded);
        var blob = EndpointFormatSupport.SerializeWaveFormat(padded!);
        Assert.Equal(40, blob.Length);
        Assert.Equal(0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(14)));
        Assert.Equal(22, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(16)));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(18)));
        Assert.Equal(PcmSubFormat, new Guid(blob.AsSpan(24, 16)));
    }

    [Fact]
    public void CreateProbeFormats_32Bit_IncludesIntegerPcmVariant()
    {
        // NAudio's extensible constructor forces IEEE float at 32 bits; the integer PCM shape
        // must come from the manual factory.
        var formats = EndpointFormatSupport.CreateProbeFormats(48000, 32, 2).ToList();
        var kinds = formats
            .Select(format => EndpointFormatSupport.CreateDescriptor(format, EndpointFormatOrigin.ExclusiveProbe).Kind)
            .ToList();

        Assert.Contains(EndpointFormatKind.WaveFormatExtensibleIeeeFloat, kinds);
        Assert.Contains(EndpointFormatKind.WaveFormatExtensible, kinds.Where((kind, index) => formats[index].BitsPerSample == 32));
    }

    [Fact]
    public void CreatePcmExtensibleFormat_RoundTripsThroughSerializeAndParse()
    {
        var format = EndpointFormatSupport.CreatePcmExtensibleFormat(96000, validBits: 24, containerBits: 32, channels: 2);

        var blob = EndpointFormatSupport.SerializeWaveFormat(format);
        var descriptor = EndpointFormatSupport.TryParseDeviceFormat(blob, EndpointFormatOrigin.ExclusiveProbe);

        Assert.NotNull(descriptor);
        Assert.Equal(new AudioFormatCandidate(96000, 24, 2), descriptor!.Format);
        Assert.Equal(EndpointFormatKind.WaveFormatExtensiblePadded, descriptor.Kind);
        Assert.Equal(32, descriptor.NativeFormat.BitsPerSample);
    }

    [Fact]
    public void TryParseDeviceFormat_24In32PropertyStoreBlob_ReportsValidBits()
    {
        // Simulates the property-store format of an affected DAC (24 valid bits, 32-bit container).
        var blob = Build24In32Blob(48000, 2);

        var descriptor = EndpointFormatSupport.TryParseDeviceFormat(blob);

        Assert.NotNull(descriptor);
        Assert.Equal(24, descriptor!.Format.BitDepth);
        Assert.Equal(48000, descriptor.Format.SampleRateHz);
        Assert.Equal(EndpointFormatKind.WaveFormatExtensiblePadded, descriptor.Kind);
    }

    [Fact]
    public void CreateDescriptor_ClassifiesNAudioExtensibleVariants()
    {
        var floatDescriptor = EndpointFormatSupport.CreateDescriptor(
            new WaveFormatExtensible(48000, 32, 2), EndpointFormatOrigin.ExclusiveProbe);
        var pcmDescriptor = EndpointFormatSupport.CreateDescriptor(
            new WaveFormatExtensible(44100, 24, 2), EndpointFormatOrigin.ExclusiveProbe);

        Assert.Equal(EndpointFormatKind.WaveFormatExtensibleIeeeFloat, floatDescriptor.Kind);
        Assert.Equal(32, floatDescriptor.Format.BitDepth);
        Assert.Equal(EndpointFormatKind.WaveFormatExtensible, pcmDescriptor.Kind);
        Assert.Equal(24, pcmDescriptor.Format.BitDepth);
    }

    [Fact]
    public void BuildSupportedFormats_FiioAcceptanceProfile_Yields24BitCandidates()
    {
        // Acceptance profile observed on affected DACs: plain 16-bit PCM and 24-in-32 only.
        static bool IsSupported(WaveFormat format) =>
            format is not WaveFormatExtensible && format.BitsPerSample == 16 ||
            format is WaveFormatExtensible && format.BitsPerSample == 32 &&
                EndpointFormatSupport.GetValidBitsPerSample(format) == 24;

        var descriptors = EndpointFormatSupport.BuildSupportedFormats(
            currentDeviceFormat: null,
            channels: 2,
            candidateRates: [44100, 48000],
            candidateDepths: [16, 24, 32],
            IsSupported);

        var candidates = descriptors.Select(descriptor => descriptor.Format).Distinct().ToList();
        Assert.Contains(new AudioFormatCandidate(48000, 24, 2), candidates);
        Assert.Contains(new AudioFormatCandidate(44100, 24, 2), candidates);
        Assert.Contains(new AudioFormatCandidate(48000, 16, 2), candidates);
    }

    [Fact]
    public void BuildSupportedFormats_PaddedAndPackedVariants_StayDistinct()
    {
        static bool IsSupported(WaveFormat format) =>
            format is WaveFormatExtensible && EndpointFormatSupport.GetValidBitsPerSample(format) == 24;

        var descriptors = EndpointFormatSupport.BuildSupportedFormats(
            currentDeviceFormat: null,
            channels: 2,
            candidateRates: [48000],
            candidateDepths: [24],
            IsSupported);

        Assert.Equal(2, descriptors.Count);
        Assert.Contains(descriptors, descriptor => descriptor.Kind == EndpointFormatKind.WaveFormatExtensible);
        Assert.Contains(descriptors, descriptor => descriptor.Kind == EndpointFormatKind.WaveFormatExtensiblePadded);
        Assert.All(descriptors, descriptor => Assert.Equal(new AudioFormatCandidate(48000, 24, 2), descriptor.Format));
    }

    [Fact]
    public void SelectContainerBitsFallback_MatchesLegacy32BitTargetToPaddedDescriptor()
    {
        // A pre-fix OriginalTarget snapshot stored the container size (32) of a 24-in-32 device.
        var padded = EndpointFormatSupport.CreateDescriptor(
            EndpointFormatSupport.CreatePcmExtensibleFormat(48000, 24, 32, 2),
            EndpointFormatOrigin.ExclusiveProbe);
        var sixteen = EndpointFormatSupport.CreateDescriptor(
            new WaveFormat(48000, 16, 2), EndpointFormatOrigin.ExclusiveProbe);

        var selected = EndpointFormatSupport.SelectContainerBitsFallback(
            [sixteen, padded],
            new AudioFormatCandidate(48000, 32, 2));

        Assert.Same(padded, selected);
    }

    [Fact]
    public void VerifyTargetFormat_SucceedsWhenDeviceReports24In32()
    {
        var reported = EndpointFormatSupport.TryParseDeviceFormat(Build24In32Blob(48000, 2))!.Format;

        var result = EndpointFormatSupport.VerifyTargetFormat(
            new AudioFormatCandidate(48000, 24, 2),
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero,
            () => new DeviceFormatSnapshot(reported, null),
            _ => { });

        Assert.True(result.Success);
        Assert.Equal("property store", result.VerificationSource);
    }

    private static byte[] Build24In32Blob(int sampleRate, int channels)
    {
        var blockAlign = channels * 4;
        var blob = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(18), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(20), (uint)((1 << channels) - 1));
        PcmSubFormat.TryWriteBytes(blob.AsSpan(24));
        return blob;
    }
}
