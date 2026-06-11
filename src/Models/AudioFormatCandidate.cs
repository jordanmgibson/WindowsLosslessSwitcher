namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Represents a concrete sample-rate, bit-depth, and channel-count combination.
/// </summary>
public sealed record AudioFormatCandidate(
    int SampleRateHz,
    int BitDepth,
    int Channels)
{
    /// <summary>
    /// Returns a compact display label used in diagnostics and UI.
    /// </summary>
    public string DisplayName => $"{BitDepth}/{SampleRateHz / 1000.0:0.###}";
}
