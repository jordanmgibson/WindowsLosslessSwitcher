using System.Diagnostics;
using System.Windows.Automation;
using WindowsLosslessSwitcher.Abstractions;

namespace WindowsLosslessSwitcher.Services;

public sealed class AppleMusicProcessController : IAppleMusicProcessController
{
    private const string AppleMusicProcessName = "AppleMusic";
    private const string AppleMediaAgentProcessName = "AMPLibraryAgent";

    // Window for the graceful close to complete before the process is killed. The close request
    // must go through UI Automation's WindowPattern: Apple Music ignores the WM_CLOSE that
    // Process.CloseMainWindow posts (verified live), but honors the UIA close like a click on X.
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RelaunchAppearTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ProcessPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly DiagnosticsLogger _logger;

    public AppleMusicProcessController(DiagnosticsLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> TryRestartAsync(CancellationToken cancellationToken)
    {
        // Cancellation is only observed before the close begins: aborting between the close and
        // the relaunch would leave Apple Music down, which is worse than any wedge.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await CloseAppleMusicAsync();
            KillSurvivingMediaAgent();
            return await RelaunchAppleMusicAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Apple Music restart failed: {ex.Message}");
            return false;
        }
    }

    private async Task CloseAppleMusicAsync()
    {
        var processes = Process.GetProcessesByName(AppleMusicProcessName);
        if (processes.Length == 0)
        {
            _logger.Info("Apple Music process not found; proceeding straight to relaunch.");
            return;
        }

        try
        {
            foreach (var process in processes)
            {
                if (process.MainWindowHandle != IntPtr.Zero && !TryCloseViaUiAutomation(process.MainWindowHandle))
                {
                    process.CloseMainWindow();
                }
            }

            var deadline = DateTimeOffset.UtcNow + GracefulExitTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (processes.All(process => process.HasExited))
                {
                    _logger.Info("Apple Music closed gracefully.");
                    return;
                }

                await Task.Delay(ProcessPollInterval, CancellationToken.None);
            }

            foreach (var process in processes.Where(process => !process.HasExited))
            {
                _logger.Warn("Apple Music did not close gracefully; killing the process.");
                process.Kill();
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private bool TryCloseViaUiAutomation(IntPtr mainWindowHandle)
    {
        try
        {
            var window = AutomationElement.FromHandle(mainWindowHandle);
            if (window.GetCurrentPattern(WindowPattern.Pattern) is WindowPattern windowPattern)
            {
                windowPattern.Close();
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.Info($"UIA close request failed ({ex.Message}); falling back to CloseMainWindow.");
        }

        return false;
    }

    private void KillSurvivingMediaAgent()
    {
        // The media agent outlives the app and carries the degraded renderer state with it — a
        // relaunch that reattaches to the old agent stays broken (verified live). Killing it here
        // is safe ONLY because Apple Music itself is no longer running (killing it mid-session
        // crashes Apple Music outright); the relaunch spawns a fresh agent.
        foreach (var agent in Process.GetProcessesByName(AppleMediaAgentProcessName))
        {
            try
            {
                _logger.Info("Killing the surviving media agent so the relaunch gets a fresh renderer.");
                agent.Kill();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Could not kill the media agent: {ex.Message}");
            }
            finally
            {
                agent.Dispose();
            }
        }
    }

    private async Task<bool> RelaunchAppleMusicAsync()
    {
        using var launcher = Process.Start(new ProcessStartInfo
        {
            FileName = $"shell:AppsFolder\\{AppleMusicPaths.PackageFamilyName}!App",
            UseShellExecute = true,
        });

        var deadline = DateTimeOffset.UtcNow + RelaunchAppearTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var processes = Process.GetProcessesByName(AppleMusicProcessName);
            var running = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (running)
            {
                _logger.Info("Apple Music relaunched.");
                return true;
            }

            await Task.Delay(ProcessPollInterval, CancellationToken.None);
        }

        _logger.Warn($"Apple Music did not reappear within {RelaunchAppearTimeout.TotalSeconds:0} s of relaunch.");
        return false;
    }
}
