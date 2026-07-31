using System.Runtime.InteropServices;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// COM/P-Invoke surface for WASAPI process-loopback capture (Windows 10 2004+, the app's OS
/// floor): <c>ActivateAudioInterfaceAsync</c> on the <c>VAD\Process_Loopback</c> virtual device
/// with a process-loopback activation blob. Hand-rolled rather than via NAudio because NAudio
/// 2.2.1 does not expose activation-params-based activation; NAudio is still used for
/// WAVEFORMATEX marshaling and the FFT. Mirrors the <see cref="PolicyConfigInterop"/> style.
/// All structs are Sequential with uint/IntPtr fields only, so x64 and arm64 lay out identically.
/// </summary>
internal static class ProcessLoopbackInterop
{
    public const string VirtualLoopbackDevice = "VAD\\Process_Loopback";

    public const uint ActivationTypeProcessLoopback = 1; // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
    public const uint LoopbackModeIncludeTargetProcessTree = 0; // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
    public const uint ShareModeShared = 0; // AUDCLNT_SHAREMODE_SHARED
    public const uint StreamFlagsLoopback = 0x00020000; // AUDCLNT_STREAMFLAGS_LOOPBACK
    public const uint StreamFlagsEventCallback = 0x00040000; // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
    public const uint BufferFlagsSilent = 0x2; // AUDCLNT_BUFFERFLAGS_SILENT
    public const ushort VtBlob = 65; // VT_BLOB

    public const int AudclntErrDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntErrResourcesInvalidated = unchecked((int)0x88890026);

    public static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioClientActivationParams
    {
        public uint ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    /// <summary>
    /// PROPVARIANT carrying a BLOB. Natural Sequential packing matches the native union layout
    /// on both 32/64-bit: 8 bytes of header, then cbSize (padded) and the data pointer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    /// <summary>
    /// Marker interface the completion handler must implement — without agility the activation
    /// callback fails with E_ILLEGAL_METHOD_CALL from an MTA thread.
    /// </summary>
    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject
    {
    }

    // Vtable order is load-bearing: every method is declared, in order, even the unused ones.
    // GetMixFormat/GetDevicePeriod/GetStreamLatency are NOT supported on a process-loopback
    // client and must never be called — the caller-supplied WAVEFORMATEX is authoritative.
    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig]
        int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrameCount);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint paddingFrameCount);

        [PreserveSig]
        int IsFormatSupported(uint shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr dataPointer, out uint framesToRead, out uint flags, IntPtr devicePosition, IntPtr qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint framesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint framesInNextPacket);
    }

    /// <summary>Signals when the async activation completes; agile so MTA activation works.</summary>
    public sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _completed = new(initialState: false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) =>
            _completed.Set();

        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);
    }
}
