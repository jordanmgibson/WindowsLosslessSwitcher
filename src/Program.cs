using System;
using System.Windows;
using Velopack;
using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher;

/// <summary>
/// Boots the WPF application after Velopack has processed any install/update lifecycle events.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        // Must run before App is constructed: its field initializers create the logger, which
        // creates the new data folder and would shadow files still at the legacy location.
        AppDataPaths.MigrateFromLegacyLocation();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
