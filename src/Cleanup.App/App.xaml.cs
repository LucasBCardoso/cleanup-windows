using Cleanup.App.Services;
using Microsoft.UI.Xaml;

namespace Cleanup.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    /// <summary>A <c>cleanup:</c> link the app was started with; handled once the window exists.</summary>
    public static string? PendingLink { get; set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Write("unhandled: " + e.Exception);
            e.Handled = true;
            Window?.ShowError(e.Exception.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Application.Resources can't be touched from the constructor (E_UNEXPECTED), so styles and themes load here.
        ThemeLoader.Install(Resources);
        Window = new MainWindow();
        Window.Activate();
        if (PendingLink is { } link) Window.FollowLink(link);
    }
}
