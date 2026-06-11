using System.Threading;

namespace WindowsLosslessSwitcher.Services;

public sealed class StartupInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    internal StartupInstanceGuard(string mutexName)
    {
        _mutex = new Mutex(initiallyOwned: false, mutexName);
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
            return _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
            return true;
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
