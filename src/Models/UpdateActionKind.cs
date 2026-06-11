namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Describes the next update-related action exposed to the user.
/// </summary>
public enum UpdateActionKind
{
    /// <summary>
    /// No follow-up action is currently available.
    /// </summary>
    None,

    /// <summary>
    /// Download the available installed-build update and prepare it for restart.
    /// </summary>
    DownloadAndPrepare,

    /// <summary>
    /// Restart the app to apply an already downloaded installed-build update.
    /// </summary>
    RestartToApply,

    /// <summary>
    /// Open the portable download asset for the latest release.
    /// </summary>
    OpenPortableDownload,

    /// <summary>
    /// Open the repository releases page in the default browser.
    /// </summary>
    OpenReleasesPage,
}
