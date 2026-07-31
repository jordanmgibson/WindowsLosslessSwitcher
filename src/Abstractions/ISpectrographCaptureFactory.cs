namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>A single process-loopback capture session; disposal stops the capture thread.</summary>
internal interface ISpectrographCaptureSession : IDisposable
{
    void Start();
}

/// <summary>
/// Creates capture sessions for a target process. Seam over the WASAPI process-loopback
/// interop so the monitor's lifecycle logic is unit-testable with a fake.
/// </summary>
internal interface ISpectrographCaptureFactory
{
    /// <param name="targetProcessId">PID whose process tree's audio is captured.</param>
    /// <param name="onSamples">Called on the capture thread with interleaved stereo f32 samples.</param>
    /// <param name="onStreamFailed">Called once when the session dies (device invalidated, process gone).</param>
    ISpectrographCaptureSession Create(int targetProcessId, Action<float[], int> onSamples, Action<Exception> onStreamFailed);
}
