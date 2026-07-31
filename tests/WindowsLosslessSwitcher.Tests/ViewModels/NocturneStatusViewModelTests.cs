using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.ViewModels;

/// <summary>
/// Covers the Nocturne surface state derived in <see cref="MainWindowViewModel"/>: phase
/// mapping, previous-format bookkeeping, activity log, source chip, and status chips.
/// </summary>
public sealed class NocturneStatusViewModelTests
{
    private static TrackSnapshot Track(string title = "Hotel California", string artist = "Eagles", string album = "Hell Freezes Over") =>
        new("AppleInc.AppleMusicWin", null, title, artist, album, "test", DateTimeOffset.UtcNow);

    private static ResolvedAudioFormat Resolved(
        int rate = 192000,
        int bits = 24,
        AudioFormatSource source = AudioFormatSource.CatalogManifest) =>
        new(rate, bits, ResolutionConfidence.Exact, source, "test");

    private static SwitchingStatus Status(
        string text,
        TrackSnapshot? track = null,
        ResolvedAudioFormat? requested = null,
        AudioFormatCandidate? applied = null,
        string? failure = null,
        bool changed = false) =>
        new(text, "Test Device", track, requested, applied, failure, changed);

    // ── Phase derivation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Resolver: Waiting for Apple Music", SwitchPhase.Idle)]
    [InlineData("Resolver: Detecting track", SwitchPhase.Detecting)]
    [InlineData("Resolver: Resolving format", SwitchPhase.Resolving)]
    [InlineData("Switching: Applying format", SwitchPhase.Switching)]
    [InlineData("Switching: Waiting for device", SwitchPhase.Switching)]
    [InlineData("Switching: Preparing", SwitchPhase.Switching)]
    [InlineData("Switching: Rebuilding audio pipeline", SwitchPhase.Switching)]
    [InlineData("Switching: Recovering playback", SwitchPhase.Switching)]
    [InlineData("Switching: Restarting Apple Music", SwitchPhase.Switching)]
    [InlineData("Switching: Playback restored", SwitchPhase.Restored)]
    [InlineData("Resolver: Error", SwitchPhase.Failed)]
    [InlineData("Resolver: Failed", SwitchPhase.Failed)]
    [InlineData("Resolver: No target device", SwitchPhase.Failed)]
    public void DerivePhase_MapsRawStatusText(string text, SwitchPhase expected)
    {
        Assert.Equal(expected, MainWindowViewModel.DerivePhase(Status(text)));
    }

    [Fact]
    public void DerivePhase_TerminalSourceStatusWithAppliedFormat_IsNoChange()
    {
        var status = Status(
            "Resolver: CatalogManifest",
            requested: Resolved(),
            applied: new AudioFormatCandidate(192000, 24, 2));

        Assert.Equal(SwitchPhase.NoChange, MainWindowViewModel.DerivePhase(status));
    }

    [Fact]
    public void DerivePhase_TerminalSourceStatusWithOnlyFailure_IsFailed()
    {
        var status = Status("Resolver: CatalogManifest", failure: "Device rejected format");

        Assert.Equal(SwitchPhase.Failed, MainWindowViewModel.DerivePhase(status));
    }

    [Fact]
    public void BuildPhaseText_UsesDesignCopy()
    {
        Assert.Equal(
            "Track change — output muted",
            MainWindowViewModel.BuildPhaseText(SwitchPhase.Detecting, Status("Resolver: Detecting track")));
        Assert.Equal(
            "No switch needed — format already matches",
            MainWindowViewModel.BuildPhaseText(SwitchPhase.NoChange, Status("Resolver: CatalogManifest")));
        Assert.Equal(
            "Restarting Apple Music…",
            MainWindowViewModel.BuildPhaseText(SwitchPhase.Switching, Status("Switching: Restarting Apple Music")));
    }

    [Fact]
    public void UpdateStatus_BusyPhaseDrivesDot()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.UpdateStatus(Status("Resolver: Detecting track", Track()));
        Assert.True(viewModel.IsBusyPhase);

        viewModel.UpdateStatus(Status(
            "Switching: Playback restored",
            Track(),
            Resolved(),
            new AudioFormatCandidate(192000, 24, 2),
            changed: true));
        Assert.False(viewModel.IsBusyPhase);
        Assert.Equal("Playback restored", viewModel.PhaseText);
    }

    // ── Previous applied format / "was …" line ────────────────────────────────

    [Fact]
    public void UpdateStatus_TracksPreviousFormatAcrossSwitches()
    {
        var viewModel = new MainWindowViewModel();
        var first = new AudioFormatCandidate(44100, 24, 2);
        var second = new AudioFormatCandidate(192000, 24, 2);

        viewModel.UpdateCurrentDeviceFormat(first);
        viewModel.UpdateStatus(Status(
            "Switching: Playback restored", Track(), Resolved(), second, changed: true));

        Assert.Equal(first, viewModel.PreviousAppliedFormat);
        Assert.Equal("was 24-bit / 44.1 kHz", viewModel.WasLine);
        Assert.Equal("24-bit / 192 kHz", viewModel.AppliedFormatDisplayText);
    }

    [Fact]
    public void UpdateStatus_IntermediateSwitchingStatusDoesNotEraseThePreviousFormat()
    {
        var viewModel = new MainWindowViewModel();
        var oldFormat = new AudioFormatCandidate(44100, 16, 2);
        var newFormat = new AudioFormatCandidate(48000, 16, 2);
        viewModel.UpdateCurrentDeviceFormat(oldFormat);

        // The coordinator publishes the NEW format on "Waiting for device" (not changed yet),
        // then the terminal restored status. The toast reads PreviousAppliedFormat afterwards.
        viewModel.UpdateStatus(Status("Switching: Applying format", Track(), Resolved(48000, 16)));
        viewModel.UpdateStatus(Status("Switching: Waiting for device", Track(), Resolved(48000, 16), newFormat));
        viewModel.UpdateStatus(Status("Switching: Playback restored", Track(), Resolved(48000, 16), newFormat, changed: true));

        Assert.Equal(oldFormat, viewModel.PreviousAppliedFormat);
        Assert.Equal("was 16-bit / 44.1 kHz", viewModel.WasLine);
    }

    [Fact]
    public void UpdateStatus_NoChangeShowsCarriedOverLine()
    {
        var viewModel = new MainWindowViewModel();
        var format = new AudioFormatCandidate(96000, 24, 2);

        viewModel.UpdateCurrentDeviceFormat(format);
        viewModel.UpdateStatus(Status("Resolver: CatalogManifest", Track(), Resolved(96000), format));

        Assert.Equal("format carried over unchanged", viewModel.WasLine);
    }

    // ── Activity log ──────────────────────────────────────────────────────────

    [Fact]
    public void UpdateStatus_SwitchAddsActivityEntryOnce()
    {
        var viewModel = new MainWindowViewModel();
        var status = Status(
            "Switching: Playback restored",
            Track(),
            Resolved(),
            new AudioFormatCandidate(192000, 24, 2),
            changed: true);

        viewModel.UpdateStatus(status);
        viewModel.UpdateStatus(status);

        var entry = Assert.Single(viewModel.ActivityLog);
        Assert.Equal("Switched to 24-bit / 192 kHz for “Hotel California”", entry.Text);
    }

    [Fact]
    public void AddActivity_CapsAtEightNewestFirst()
    {
        var viewModel = new MainWindowViewModel();
        for (var i = 0; i < 10; i++)
        {
            viewModel.AddActivity($"entry {i}", DateTimeOffset.Now);
        }

        Assert.Equal(8, viewModel.ActivityLog.Count);
        Assert.Equal("entry 9", viewModel.ActivityLog[0].Text);
        Assert.Equal("entry 2", viewModel.ActivityLog[^1].Text);
    }

    // ── Source chip ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioFormatSource.CatalogManifest, "Catalog match")]
    [InlineData(AudioFormatSource.CachedCatalog, "Catalog match")]
    [InlineData(AudioFormatSource.LocalFile, "Local file")]
    [InlineData(AudioFormatSource.TierFallback, "Fallback")]
    public void UpdateStatus_MapsSourceChip(AudioFormatSource source, string expected)
    {
        var viewModel = new MainWindowViewModel();

        viewModel.UpdateStatus(Status("Resolver: Resolving format", Track(), Resolved(source: source)));

        Assert.Equal(expected, viewModel.SourceChipText);
        Assert.Equal(source.ToString(), viewModel.SourceRawText);
    }

    // ── Status chips ──────────────────────────────────────────────────────────

    [Fact]
    public void StatusChips_ReflectAppliedFormatAndAllowList()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SeedAllowedFormats([44100, 192000], []);
        viewModel.UpdateActiveTargetCapabilities(new CurrentTargetDeviceCapabilitiesSnapshot(
            "Test Device",
            [
                new AudioFormatCandidate(44100, 16, 2),
                new AudioFormatCandidate(44100, 24, 2),
                new AudioFormatCandidate(96000, 24, 2),
                new AudioFormatCandidate(192000, 24, 2),
            ]));
        viewModel.UpdateStatus(Status(
            "Switching: Playback restored",
            Track(),
            Resolved(),
            new AudioFormatCandidate(192000, 24, 2),
            changed: true));

        Assert.Equal(3, viewModel.StatusRateChips.Count);
        var active = Assert.Single(viewModel.StatusRateChips, chip => chip.IsActive);
        Assert.Equal("192 kHz", active.Label);
        var excluded = Assert.Single(viewModel.StatusRateChips, chip => !chip.IsAllowed);
        Assert.Equal("96 kHz", excluded.Label);
        Assert.Equal(2, viewModel.StatusBitChips.Count);
        Assert.All(viewModel.StatusBitChips, chip => Assert.True(chip.IsAllowed));
    }

    // ── Misc display state ────────────────────────────────────────────────────

    [Fact]
    public void DeviceModeChipText_FollowsSelectedMode()
    {
        var viewModel = new MainWindowViewModel();
        Assert.Equal("Follows default", viewModel.DeviceModeChipText);

        viewModel.SelectedMode = DeviceSelectionMode.PinnedDevice;
        Assert.Equal("Pinned", viewModel.DeviceModeChipText);
    }

    [Fact]
    public void StorefrontNote_ShowsDetectedThenOverride()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SeedStorefrontInfo("us");
        Assert.Equal("Detected from the Windows region: us", viewModel.StorefrontNoteText);

        viewModel.AppleMusicStorefront = "GB";
        Assert.Equal("Override: “gb”", viewModel.StorefrontNoteText);
    }

    [Fact]
    public void TrackMeta_FormatsArtistAndAlbum()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.UpdateStatus(Status("Resolver: Detecting track", Track()));
        Assert.Equal("Hotel California", viewModel.TrackTitleText);
        Assert.Equal("Eagles — Hell Freezes Over", viewModel.TrackMetaText);

        viewModel.UpdateStatus(Status("Resolver: Waiting for Apple Music"));
        Assert.Equal("Nothing playing", viewModel.TrackTitleText);
    }
}
