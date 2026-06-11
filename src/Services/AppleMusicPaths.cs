using System.IO;

namespace WindowsLosslessSwitcher.Services;

public sealed class AppleMusicPaths
{
    public const string PackageFamilyName = "AppleInc.AppleMusicWin_nzyj5cx40ttqa";

    public string PackageRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            PackageFamilyName);

    public string PreferencesDirectory =>
        Path.Combine(PackageRoot, "LocalCache", "Roaming", "Apple Computer", "Preferences");

    public string LibraryAgentPreferencesPath =>
        Path.Combine(PreferencesDirectory, "AMPLibraryAgent.exe.plist");

    public string PlayCacheDirectory =>
        Path.Combine(PackageRoot, "LocalCache", "Local", "Apple", "AMPLibraryAgent", "PlayCache");
}
