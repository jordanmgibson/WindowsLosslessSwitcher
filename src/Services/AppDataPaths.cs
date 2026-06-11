using System.IO;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Defines where the app stores its data (settings.json and logs) and migrates data left behind
/// at the legacy location by older builds.
/// </summary>
public static class AppDataPaths
{
    private const string AppFolderName = "WindowsLosslessSwitcher";

    /// <summary>
    /// Root folder for app data: %APPDATA%\WindowsLosslessSwitcher.
    /// </summary>
    public static string RootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName);

    /// <summary>
    /// Data folder used by builds before 0.1.1: %LOCALAPPDATA%\WindowsLosslessSwitcher. This is
    /// also the Velopack install root (same packId), so a dev/portable run that created it makes
    /// Setup.exe report the app as already installed — which is why data moved to <see cref="RootDirectory"/>.
    /// </summary>
    public static string LegacyRootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

    /// <summary>
    /// Moves settings.json and log files from the legacy data folder into <see cref="RootDirectory"/>.
    /// Safe to call on every startup: it only moves files that exist at the legacy location and are
    /// missing at the new one, and it never touches Velopack install content (current\, packages\,
    /// Update.exe) that may share the legacy folder.
    /// </summary>
    public static void MigrateFromLegacyLocation()
        => MigrateFromLegacyLocation(LegacyRootDirectory, RootDirectory);

    internal static void MigrateFromLegacyLocation(string legacyRoot, string newRoot)
    {
        // Best effort only — this runs before the logger exists and must never block startup.
        try
        {
            if (!Directory.Exists(legacyRoot))
            {
                return;
            }

            MoveFileIfMissing(
                Path.Combine(legacyRoot, "settings.json"),
                Path.Combine(newRoot, "settings.json"));

            var legacyLogsDirectory = Path.Combine(legacyRoot, "logs");
            if (Directory.Exists(legacyLogsDirectory))
            {
                var newLogsDirectory = Path.Combine(newRoot, "logs");
                foreach (var legacyLogPath in Directory.EnumerateFiles(legacyLogsDirectory))
                {
                    MoveFileIfMissing(
                        legacyLogPath,
                        Path.Combine(newLogsDirectory, Path.GetFileName(legacyLogPath)));
                }

                if (!Directory.EnumerateFileSystemEntries(legacyLogsDirectory).Any())
                {
                    Directory.Delete(legacyLogsDirectory);
                }
            }
        }
        catch
        {
            // A locked or unreadable file is left in place; the next startup retries it.
        }
    }

    private static void MoveFileIfMissing(string sourcePath, string destinationPath)
    {
        try
        {
            if (!File.Exists(sourcePath) || File.Exists(destinationPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(sourcePath, destinationPath);
        }
        catch
        {
        }
    }
}
