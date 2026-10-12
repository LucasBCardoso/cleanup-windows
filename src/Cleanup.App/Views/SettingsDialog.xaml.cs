using Cleanup.App.Services;
using Cleanup.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace Cleanup.App.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings settings = AppServices.Shared.Settings;
    private bool loading = true;

    public SettingsDialog()
    {
        InitializeComponent();
        ThemeChoice.SelectedIndex = (int)settings.Theme;
        SupportPath.Text = AppPaths.SupportDirectory;
        CfaText.Text = ControlledFolderAccessText();
        Version.Text = $"Cleanup para Windows {typeof(App).Assembly.GetName().Version?.ToString(3)} · convertido do Cleanup para Mac";
        loading = false;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ThemeChoice.SelectedIndex < 0) return;
        settings.Theme = (Services.ThemeChoice)ThemeChoice.SelectedIndex;
        settings.Save();
        App.Window?.ApplyTheme(settings.Theme);
        RequestedTheme = settings.Theme switch
        {
            Services.ThemeChoice.Daybreak => ElementTheme.Light,
            Services.ThemeChoice.Ledger => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Controlled Folder Access can make Windows refuse moves out of protected folders; we report it, never bypass it.</summary>
    private static string ControlledFolderAccessText()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access");
            var on = key?.GetValue("EnableControlledFolderAccess") is int v && v == 1;
            return on
                ? "Ativado. O Windows pode bloquear o Cleanup em Documentos, Imagens e outras pastas protegidas; permita o Cleanup na Segurança do Windows se isso acontecer."
                : "Desativado. O Cleanup pode mover itens das suas pastas para a Lixeira normalmente.";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "Não foi possível ler o estado. Se o Windows bloquear o Cleanup, permita-o na Segurança do Windows.";
        }
    }

    private void OnOpenCfa(object sender, RoutedEventArgs e) => AppServices.Open("windowsdefender://ransomwareprotection");

    private void OnOpenSupport(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.SupportDirectory);
        AppServices.Open(AppPaths.SupportDirectory);
    }
}
