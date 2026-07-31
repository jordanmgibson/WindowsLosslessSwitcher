namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// What the mini-spectrograph control consumes: the latest normalized bar magnitudes for
/// Apple Music's audio, an activity flag, and a visibility lease that gates the capture
/// pipeline (capture only runs while at least one visible control holds a lease).
/// </summary>
public interface ISpectrumSource
{
    /// <summary>Latest bar magnitudes in 0..1 (fixed length, never null). Read-only snapshot.</summary>
    float[] CurrentBars { get; }

    /// <summary>True while Apple Music audio is being captured and was recently non-silent.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Registers a visible consumer. The pipeline starts on the first lease and fully stops
    /// (zero CPU) when the last lease is disposed.
    /// </summary>
    IDisposable AcquireVisibleLease();
}
