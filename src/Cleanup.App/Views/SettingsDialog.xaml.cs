using Cleanup.App.Services;
using Cleanup.Core.AutoClean;
using Cleanup.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings settings = AppServices.Shared.Settings;
    private bool loading = true;

    /// <summary>True when "Rever introdução" was chosen: the window shows it once this dialog has closed.</summary>
    public bool ReopenOnboarding { get; private set; }

    public SettingsDialog()
    {
        InitializeComponent();
        ThemeChoice.SelectedIndex = (int)settings.Theme;
        TraySwitch.IsOn = settings.TrayEnabled;
        NotificationsSwitch.IsOn = settings.NotificationsEnabled;
        NotificationsSystemText.Text = Notifications.IsAllowedBySystem
            ? "O Windows permite as notificações do Cleanup (Configurações › Sistema › Notificações)."
            : "As notificações do Cleanup estão desativadas em Configurações › Sistema › Notificações.";
        try { LoginSwitch.IsOn = LaunchAtLogin.IsEnabled; } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        FrequencyBox.SelectedIndex = (int)settings.AutoClean.Frequency;
        ModeBox.SelectedIndex = (int)settings.AutoClean.Mode;
        SupportPath.Text = AppPaths.SupportDirectory;
        CfaText.Text = SystemInfo.ControlledFolderAccessText();
        Version.Text = $"Cleanup para Windows {typeof(App).Assembly.GetName().Version?.ToString(3)} · convertido do Cleanup para Mac";
        UpdateAutoCleanTexts();
        loading = false;
    }

    private void UpdateAutoCleanTexts()
    {
        var auto = AppViewModels.AutoClean;
        auto?.UpdateTexts();
        LastRunText.Text = auto?.LastRunText ?? "";
        NextRunText.Text = auto?.NextRunText ?? "";
        RunNowText.Text = settings.AutoClean.Mode == AutoCleanMode.Clean ? "Limpar agora" : "Verificar agora";
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ThemeChoice.SelectedIndex < 0) return;
        settings.Theme = (Services.ThemeChoice)ThemeChoice.SelectedIndex;
        settings.Save();
        App.Window?.ApplyTheme(settings.Theme);
        RequestedTheme = App.Window?.CurrentTheme ?? ElementTheme.Default;
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        settings.TrayEnabled = TraySwitch.IsOn;
        settings.Save();
        App.EnableTray(TraySwitch.IsOn);
    }

    private void OnNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        settings.NotificationsEnabled = NotificationsSwitch.IsOn;
        settings.Save();
    }

    private void OnLoginToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        try { LaunchAtLogin.Set(LoginSwitch.IsOn); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Log.Write("login: " + ex.Message); }
    }

    private void OnAutoCleanChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || FrequencyBox.SelectedIndex < 0 || ModeBox.SelectedIndex < 0) return;
        var updated = settings.AutoClean with
        {
            Frequency = (AutoCleanFrequency)FrequencyBox.SelectedIndex,
            Mode = (AutoCleanMode)ModeBox.SelectedIndex,
        };
        if (AppViewModels.AutoClean is { } auto) auto.Settings = updated;
        else { settings.AutoClean = updated; settings.Save(); }
        UpdateAutoCleanTexts();
    }

    private async void OnRunNow(object sender, RoutedEventArgs e)
    {
        if (AppViewModels.AutoClean is not { IsRunning: false } auto) return;
        RunNowButton.IsEnabled = false;
        RunNowRing.IsActive = true;
        try { await auto.RunAsync(settings.AutoClean with { MinimumBytes = 0 }); }
        finally
        {
            RunNowRing.IsActive = false;
            RunNowButton.IsEnabled = true;
            UpdateAutoCleanTexts();
        }
    }

    private void OnOpenCfa(object sender, RoutedEventArgs e) => AppServices.Open("windowsdefender://ransomwareprotection");

    private void OnOpenSupport(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.SupportDirectory);
        AppServices.Open(AppPaths.SupportDirectory);
    }

    private void OnOnboarding(object sender, RoutedEventArgs e)
    {
        ReopenOnboarding = true;
        Hide();
    }
}
