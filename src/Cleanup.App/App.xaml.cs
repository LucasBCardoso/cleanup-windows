using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Cleanup.App.Views;
using Microsoft.UI.Xaml;

namespace Cleanup.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    public static TrayPanel? Panel { get; private set; }
    private static TrayIcon? tray;

    /// <summary>A <c>cleanup:</c> link the app was started with; handled once the window exists.</summary>
    public static string? PendingLink { get; set; }

    /// <summary>Started at login: stay in the tray until the user opens the window.</summary>
    public static bool StartInBackground { get; set; }

    /// <summary>Set by "Sair": closing the window then really quits instead of hiding to the tray.</summary>
    public static bool IsExiting { get; private set; }

    private const int MenuOpen = 1, MenuScan = 2, MenuHistory = 3, MenuQuit = 9;

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
        var settings = AppServices.Shared.Settings;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        AppViewModels.Health = new HealthViewModel(dispatcher);
        AppViewModels.AutoClean = new AutoCleanViewModel(dispatcher, AppViewModels.Health, AppViewModels.Scan);
        Window = new MainWindow();

        AppViewModels.Health.Sampled += () => { if (tray is not null) tray.Tooltip = AppViewModels.Health.TrayTooltip(); };
        AppViewModels.Health.Start();
        AppViewModels.AutoClean.Start();
        AppViewModels.SelfUpdate = new SelfUpdateViewModel(dispatcher);
        AppViewModels.SelfUpdate.Start();

        Notifications.Register(link => Window.DispatcherQueue.TryEnqueue(() => ShowMainWindow(link)));
        if (settings.TrayEnabled) EnableTray(true);

        if (!(StartInBackground && settings.TrayEnabled)) Window.Activate();
        if (PendingLink is { } link) ShowMainWindow(link);
        if (!settings.OnboardingDone && !StartInBackground) Window.ShowOnboardingWhenReady();
    }

    /// <summary>Shows the tray icon (or removes it). Without it, closing the window quits.</summary>
    public static void EnableTray(bool enabled)
    {
        if (enabled && tray is null)
        {
            tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Cleanup.ico"),
                [(MenuOpen, "Abrir Cleanup"), (MenuScan, "Verificar agora"), (MenuHistory, "Histórico de limpezas"), (0, "-"), (MenuQuit, "Sair do Cleanup")]);
            tray.Clicked += () => Window?.DispatcherQueue.TryEnqueue(TogglePanel);
            tray.MenuChosen += id => Window?.DispatcherQueue.TryEnqueue(() =>
            {
                switch (id)
                {
                    case MenuOpen: ShowMainWindow(); break;
                    case MenuScan: ShowMainWindow("cleanup://scan"); break;
                    case MenuHistory: ShowMainWindow("cleanup://history"); break;
                    case MenuQuit: Quit(); break;
                }
            });
        }
        else if (!enabled && tray is not null)
        {
            tray.Dispose();
            tray = null;
            Panel?.AppWindow.Hide();
        }
    }

    public static bool HasTray => tray is not null;

    /// <summary>The tray panel from inside the app (Hoje's Saúde tile), even with the tray icon off.</summary>
    public static void ShowHealthPanel()
    {
        Panel ??= new TrayPanel();
        if (Window is not null) Panel.ApplyTheme(Window.CurrentTheme);
        Panel.ShowNearTray();
    }

    private static void TogglePanel()
    {
        Panel ??= new TrayPanel();
        if (Panel.AppWindow.IsVisible) { Panel.AppWindow.Hide(); return; }
        if (Window is not null) Panel.ApplyTheme(Window.CurrentTheme);
        Panel.ShowNearTray();
    }

    public static void ShowMainWindow(string? link = null)
    {
        if (Window is null) return;
        Window.AppWindow.Show();
        Window.FollowLink(link);
    }

    public static void Quit()
    {
        if (IsExiting) return;
        AppViewModels.SelfUpdate?.InstallOnQuit();
        IsExiting = true;
        tray?.Dispose();
        tray = null;
        Notifications.Unregister();
        Panel?.Close();
        Window?.Close();
        Current.Exit();
    }
}
