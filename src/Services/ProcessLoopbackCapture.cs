using System.Runtime.InteropServices;
using NAudio.Wave;
using WindowsLosslessSwitcher.Abstractions;
using static WindowsLosslessSwitcher.Services.ProcessLoopbackInterop;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// One process-loopback capture session on a dedicated background thread. The audio engine
/// mixes the target process tree's streams into the caller-specified format (48 kHz / f32 /
/// stereo here), so the DSP configuration never changes even as the render device is switched
/// between 44.1 and 384 kHz. Every IPolicyConfig format switch invalidates the stream —
/// <c>AUDCLNT_E_DEVICE_INVALIDATED</c> / <c>RESOURCES_INVALIDATED</c> are routine here, and the
/// session just reports the failure and lets <see cref="AppleMusicAudioMonitor"/> restart it.
/// </summary>
internal sealed class ProcessLoopbackCapture : ISpectrographCaptureSession
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    // 200 ms engine buffer in 100 ns units.
    private const long BufferDurationHns = 2_000_000;

    private readonly int _targetProcessId;
    private readonly Action<float[], int> _onSamples;
    private readonly Action<Exception> _onStreamFailed;
    private readonly ManualResetEventSlim _stopRequested = new(initialState: false);
    private Thread? _thread;
    private volatile bool _disposed;

    public ProcessLoopbackCapture(int targetProcessId, Action<float[], int> onSamples, Action<Exception> onStreamFailed)
    {
        _targetProcessId = targetProcessId;
        _onSamples = onSamples;
        _onStreamFailed = onStreamFailed;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Capture session already started.");
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WLS-Spectrograph",
        };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            RunCapture();
        }
        catch (Exception ex)
        {
            if (!_stopRequested.IsSet)
            {
                _onStreamFailed(ex);
            }
        }
    }

    private void RunCapture()
    {
        // Everything is prepared before the async activation so the initialize sequence can run
        // immediately once the completion handler releases us.
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        var formatPtr = IntPtr.Zero;
        var activationParamsPtr = IntPtr.Zero;
        var propVariantPtr = IntPtr.Zero;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        var started = false;

        try
        {
            formatPtr = WaveFormat.MarshalToPtr(waveFormat);

            var activationParams = new AudioClientActivationParams
            {
                ActivationType = ActivationTypeProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = (uint)_targetProcessId,
                    ProcessLoopbackMode = LoopbackModeIncludeTargetProcessTree,
                },
            };
            var activationParamsSize = Marshal.SizeOf<AudioClientActivationParams>();
            activationParamsPtr = Marshal.AllocHGlobal(activationParamsSize);
            Marshal.StructureToPtr(activationParams, activationParamsPtr, fDeleteOld: false);

            var propVariant = new PropVariantBlob
            {
                Vt = VtBlob,
                BlobSize = (uint)activationParamsSize,
                BlobData = activationParamsPtr,
            };
            propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            Marshal.StructureToPtr(propVariant, propVariantPtr, fDeleteOld: false);

            var handler = new ActivationCompletionHandler();
            var audioClientIid = AudioClientIid;
            Check(ActivateAudioInterfaceAsync(
                VirtualLoopbackDevice,
                ref audioClientIid,
                propVariantPtr,
                handler,
                out var operation));

            if (!handler.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Process-loopback activation timed out.");
            }

            operation.GetActivateResult(out var activateResult, out var activated);
            Check(activateResult);
            audioClient = (IAudioClient)activated!;

            using var frameEvent = new AutoResetEvent(initialState: false);
            Check(audioClient.Initialize(
                ShareModeShared,
                StreamFlagsLoopback | StreamFlagsEventCallback,
                BufferDurationHns,
                0,
                formatPtr,
                IntPtr.Zero));
            Check(audioClient.SetEventHandle(frameEvent.SafeWaitHandle.DangerousGetHandle()));

            var captureIid = AudioCaptureClientIid;
            Check(audioClient.GetService(ref captureIid, out var captureObject));
            captureClient = (IAudioCaptureClient)captureObject;

            Check(audioClient.Start());
            started = true;

            var sampleBuffer = new float[SampleRate / 4 * Channels];
            while (!_stopRequested.IsSet)
            {
                // Hybrid wait+drain: the loopback event only fires when packets arrive and is
                // known to stall on some Windows 10 builds, so liveness never depends on it.
                frameEvent.WaitOne(20);
                DrainPackets(captureClient, ref sampleBuffer);
            }
        }
        finally
        {
            if (started)
            {
                try
                {
                    audioClient!.Stop();
                }
                catch
                {
                    // The stream may already be invalidated; teardown continues regardless.
                }
            }

            if (captureClient is not null)
            {
                Marshal.ReleaseComObject(captureClient);
            }

            if (audioClient is not null)
            {
                Marshal.ReleaseComObject(audioClient);
            }

            if (propVariantPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(propVariantPtr);
            }

            if (activationParamsPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(activationParamsPtr);
            }

            if (formatPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(formatPtr);
            }
        }
    }

    private void DrainPackets(IAudioCaptureClient captureClient, ref float[] sampleBuffer)
    {
        while (true)
        {
            Check(captureClient.GetNextPacketSize(out var packetFrames));
            if (packetFrames == 0)
            {
                return;
            }

            Check(captureClient.GetBuffer(out var dataPointer, out var frames, out var flags, IntPtr.Zero, IntPtr.Zero));
            try
            {
                var sampleCount = (int)frames * Channels;
                if (sampleCount > sampleBuffer.Length)
                {
                    sampleBuffer = new float[sampleCount];
                }

                if ((flags & BufferFlagsSilent) != 0)
                {
                    Array.Clear(sampleBuffer, 0, sampleCount);
                }
                else
                {
                    Marshal.Copy(dataPointer, sampleBuffer, 0, sampleCount);
                }

                _onSamples(sampleBuffer, sampleCount);
            }
            finally
            {
                Check(captureClient.ReleaseBuffer(frames));
            }
        }
    }

    private static void Check(int hresult) => Marshal.ThrowExceptionForHR(hresult);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopRequested.Set();
        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            // A capture thread stuck in a driver call is left to exit on its own (IsBackground);
            // the stop event is deliberately never disposed so the thread can still read it.
            _thread.Join(TimeSpan.FromSeconds(1));
        }
    }
}

/// <summary>Production factory over <see cref="ProcessLoopbackCapture"/>.</summary>
internal sealed class ProcessLoopbackCaptureFactory : ISpectrographCaptureFactory
{
    public ISpectrographCaptureSession Create(
        int targetProcessId,
        Action<float[], int> onSamples,
        Action<Exception> onStreamFailed) =>
        new ProcessLoopbackCapture(targetProcessId, onSamples, onStreamFailed);
}
