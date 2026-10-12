using Cleanup.App.Services;
using Cleanup.Core.AutoClean;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace Cleanup.App.Views;

public sealed partial class OnboardingDialog : ContentDialog
{
    private const int Steps = 5;
    private readonly AppSettings settings = AppServices.Shared.Settings;
    private int step;

    /// <summary>True when the user chose "Fazer a primeira verificação".</summary>
    public bool StartScan { get; private set; }

    public OnboardingDialog()
    {
        InitializeComponent();
        CfaText.Text = SystemInfo.ControlledFolderAccessText();
        NotificationsSwitch.IsOn = settings.NotificationsEnabled;
        TraySwitch.IsOn = settings.TrayEnabled;
        LoginSwitch.IsOn = LaunchAtLogin.IsEnabled;
        FrequencyChoice.SelectedIndex = (int)settings.AutoClean.Frequency;
        ModeChoice.SelectedIndex = (int)settings.AutoClean.Mode;
        PrimaryButtonClick += OnPrimary;
        SecondaryButtonClick += OnBack;
        Closed += (_, _) => Finish();
        Render();
    }

    private void Render()
    {
        UIElement[] pages = [Step0, Step1, Step2, Step3, Step4];
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButtonText = step switch { 0 => "Começar", Steps - 1 => "Fazer a primeira verificação", _ => "Continuar" };
        SecondaryButtonText = step is > 0 and < Steps - 1 ? "Voltar" : "";
        CloseButtonText = step == Steps - 1 ? "Fechar" : "Pular";
        for (var i = 0; i < Dots.Children.Count; i++)
        {
            var dot = (Rectangle)Dots.Children[i];
            dot.Width = i == step ? 22 : 8;
            dot.Opacity = i == step ? 1 : 0.28;
        }
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (step < Steps - 1)
        {
            args.Cancel = true;
            Apply();
            step++;
            Render();
            return;
        }
        StartScan = true;
    }

    private void OnBack(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (step > 0) step--;
        Render();
    }

    /// <summary>Saves what the steps seen so far chose (skipping keeps the defaults for the rest).</summary>
    private void Apply()
    {
        if (step >= 2)
        {
            settings.NotificationsEnabled = NotificationsSwitch.IsOn;
            settings.TrayEnabled = TraySwitch.IsOn;
            App.EnableTray(TraySwitch.IsOn);
            try { LaunchAtLogin.Set(LoginSwitch.IsOn); } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { Log.Write("login: " + e.Message); }
        }
        if (step >= 3)
        {
            settings.AutoClean = settings.AutoClean with
            {
                Frequency = (AutoCleanFrequency)Math.Max(0, FrequencyChoice.SelectedIndex),
                Mode = (AutoCleanMode)Math.Max(0, ModeChoice.SelectedIndex),
            };
        }
        settings.Save();
    }

    private void Finish()
    {
        Apply();
        settings.OnboardingDone = true;
        settings.Save();
        AppViewModels.AutoClean?.UpdateTexts();
    }

    private void OnOpenCfa(object sender, RoutedEventArgs e) => AppServices.Open("windowsdefender://ransomwareprotection");
}
