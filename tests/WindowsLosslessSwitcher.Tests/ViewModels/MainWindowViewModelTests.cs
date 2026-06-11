using System.ComponentModel;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
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
}
