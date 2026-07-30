using System.ComponentModel;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void ClearFormatCacheCommand_RaisesRequest()
    {
        var viewModel = new MainWindowViewModel();
        var requested = false;
        viewModel.ClearFormatCacheRequested += () => requested = true;

        viewModel.ClearFormatCacheCommand.Execute(null);

        Assert.True(requested);
    }

    [Fact]
    public void UpdateAppVersion_RaisesHasUpdatePrimaryActionOncePerRefresh()
    {
        var viewModel = new MainWindowViewModel();
        var hasUpdatePrimaryActionNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.HasUpdatePrimaryAction))
            {
                hasUpdatePrimaryActionNotifications++;
            }
        };

        viewModel.UpdateAppVersion(new UpdateStatusSnapshot(
            "0.1.0",
            "Version 0.2.0 is available.",
            UpdateActionKind.DownloadAndPrepare,
            "Download update",
            true,
            true,
            true,
            false,
            false,
            "0.2.0"));

        Assert.Equal(1, hasUpdatePrimaryActionNotifications);
        Assert.True(viewModel.HasUpdatePrimaryAction);
        Assert.Equal("Download update", viewModel.UpdatePrimaryActionText);
        Assert.True(viewModel.CanRunUpdatePrimaryAction);
    }

    [Fact]
    public void UpdateAppVersion_HidesPrimaryActionWhenOnlyReleasesPageIsAvailable()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.UpdateAppVersion(new UpdateStatusSnapshot(
            "0.1.0",
            "Portable build detected.",
            UpdateActionKind.OpenReleasesPage,
            "Open releases",
            true,
            true,
            true,
            false,
            true));

        Assert.False(viewModel.HasUpdatePrimaryAction);
        Assert.False(viewModel.CanRunUpdatePrimaryAction);
        Assert.True(viewModel.CanOpenReleasesPage);
        Assert.Equal("Open releases", viewModel.UpdatePrimaryActionText);
    }

    // ── Allowed hardware formats ──────────────────────────────────────────────

    [Fact]
    public void UpdateActiveTargetCapabilities_BuildsCheckedOptionsForUnrestrictedConfig()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.UpdateActiveTargetCapabilities(CreateSnapshot());

        Assert.Equal([44100, 48000, 96000], viewModel.SampleRateOptions.Select(option => option.Value));
        Assert.Equal([16, 24], viewModel.BitDepthOptions.Select(option => option.Value));
        Assert.All(viewModel.SampleRateOptions, option => Assert.True(option.IsChecked));
        Assert.All(viewModel.BitDepthOptions, option => Assert.True(option.IsChecked));
        Assert.Empty(viewModel.AllowedSampleRates);
        Assert.Empty(viewModel.AllowedBitDepths);
        Assert.True(viewModel.HasFormatOptions);
    }

    [Fact]
    public void UncheckingOption_PersistsRemainingValuesAndNotifies()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.UpdateActiveTargetCapabilities(CreateSnapshot());
        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        viewModel.SampleRateOptions.Single(option => option.Value == 96000).IsChecked = false;

        Assert.Equal([44100, 48000], viewModel.AllowedSampleRates);
        Assert.Contains(nameof(MainWindowViewModel.AllowedSampleRates), notified);

        // Re-checking everything returns to unrestricted (empty list).
        viewModel.SampleRateOptions.Single(option => option.Value == 96000).IsChecked = true;
        Assert.Empty(viewModel.AllowedSampleRates);
    }

    [Fact]
    public void UncheckingLastOption_RevertsWithoutPersistenceNotification()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.UpdateActiveTargetCapabilities(CreateSnapshot());
        foreach (var option in viewModel.BitDepthOptions.Where(option => option.Value != 24))
        {
            option.IsChecked = false;
        }

        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        viewModel.BitDepthOptions.Single(option => option.Value == 24).IsChecked = false;

        Assert.True(viewModel.BitDepthOptions.Single(option => option.Value == 24).IsChecked);
        Assert.DoesNotContain(nameof(MainWindowViewModel.AllowedBitDepths), notified);
        Assert.Equal([24], viewModel.AllowedBitDepths);
    }

    [Fact]
    public void CapabilityRebuild_PreservesCheckedStateAndRaisesNoPersistenceNotification()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.UpdateActiveTargetCapabilities(CreateSnapshot());
        viewModel.SampleRateOptions.Single(option => option.Value == 96000).IsChecked = false;
        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        // Same snapshot again — the status-change path re-fires this constantly.
        viewModel.UpdateActiveTargetCapabilities(CreateSnapshot());

        Assert.False(viewModel.SampleRateOptions.Single(option => option.Value == 96000).IsChecked);
        Assert.Equal([44100, 48000], viewModel.AllowedSampleRates);
        Assert.DoesNotContain(nameof(MainWindowViewModel.AllowedSampleRates), notified);
        Assert.DoesNotContain(nameof(MainWindowViewModel.AllowedBitDepths), notified);
    }

    [Fact]
    public void SeededConfig_ChecksOnlyConfiguredValues()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SeedAllowedFormats([44100, 48000], [24]);

        viewModel.UpdateActiveTargetCapabilities(CreateSnapshot());

        Assert.False(viewModel.SampleRateOptions.Single(option => option.Value == 96000).IsChecked);
        Assert.True(viewModel.SampleRateOptions.Single(option => option.Value == 44100).IsChecked);
        Assert.False(viewModel.BitDepthOptions.Single(option => option.Value == 16).IsChecked);
        Assert.True(viewModel.BitDepthOptions.Single(option => option.Value == 24).IsChecked);
    }

    [Theory]
    [InlineData("  GB  ", "gb")]
    [InlineData("jp", "jp")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void AppleMusicStorefront_NormalizesOnSet(string input, string? expected)
    {
        var viewModel = new MainWindowViewModel { AppleMusicStorefront = input };

        Assert.Equal(expected, viewModel.AppleMusicStorefront);
    }

    private static CurrentTargetDeviceCapabilitiesSnapshot CreateSnapshot() =>
        new(
            "USB DAC",
            [
                new AudioFormatCandidate(44100, 16, 2),
                new AudioFormatCandidate(44100, 24, 2),
                new AudioFormatCandidate(48000, 24, 2),
                new AudioFormatCandidate(96000, 24, 2),
            ]);
}
