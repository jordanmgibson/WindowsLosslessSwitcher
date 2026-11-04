using System.Threading;

namespace WindowsLosslessSwitcher.Services;

public sealed class StartupInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _activationName;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsMutex;

    internal StartupInstanceGuard(string mutexName)
    {
        _mutex = new Mutex(initiallyOwned: false, mutexName);
        _activationName = mutexName + ".Activate";
    }

    public StartupInstanceGuard()
        : this(@"Local\WindowsLosslessSwitcher.Instance")
    {
    }

    public bool TryAcquire()
    {
        if (_ownsMutex)
        {
            return true;
        }

        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        if (_ownsMutex)
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _activationName);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationEvent,
                (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, false);
        }
        return _ownsMutex;
    }

    public event EventHandler? ActivationRequested;

    public bool RequestActivation()
    {
        try { using var signal = EventWaitHandle.OpenExisting(_activationName); return signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
