using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// Coordinates update checks and update actions for installed and portable builds.
/// </summary>
public interface IAppUpdater
{
    /// <summary>
    /// Raised whenever the visible update state changes.
    /// </summary>
    event EventHandler<UpdateStatusSnapshot>? StatusChanged;

    /// <summary>
    /// Gets the latest known updater state.
    /// </summary>
    UpdateStatusSnapshot CurrentStatus { get; }

    /// <summary>
    /// Initializes update state after application startup.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checks for an available update.
    /// </summary>
    Task CheckForUpdatesAsync(bool userInitiated, CancellationToken cancellationToken);

    /// <summary>
    /// Executes the primary update action exposed by <see cref="CurrentStatus"/>.
    /// </summary>
    Task RunPrimaryActionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens the repository releases page.
    /// </summary>
    void OpenReleasesPage();
}
