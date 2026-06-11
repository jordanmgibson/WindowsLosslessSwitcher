namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Represents the app's current update-checking state as shown in the UI and tray menu.
/// </summary>
public sealed record UpdateStatusSnapshot(
    string CurrentVersion,
    string StatusText,
    UpdateActionKind PrimaryActionKind,
    string? PrimaryActionLabel,
    bool CanCheckForUpdates,
    bool CanRunPrimaryAction,
    bool CanOpenReleasesPage,
    bool IsBusy,
    bool IsPortableBuild,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    int? ProgressPercent = null)
{
    /// <summary>
    /// Returns a default status for an unconfigured or development-time updater instance.
    /// </summary>
    public static UpdateStatusSnapshot CreateDefault(string currentVersion) =>
        new(
            currentVersion,
            "Updates are not configured yet.",
            UpdateActionKind.None,
            null,
            false,
            false,
            false,
            false,
            false);
}
