namespace WindowsLosslessSwitcher.ViewModels;

/// <summary>
/// Friendly lifecycle phase derived from <see cref="Services.SwitchingStatus"/>, driving the
/// status dot (pulsing while busy) and the human phase copy on every Nocturne surface.
/// </summary>
public enum SwitchPhase
{
    Idle,
    Detecting,
    Resolving,
    Switching,
    Restored,
    NoChange,
    Failed,
}
