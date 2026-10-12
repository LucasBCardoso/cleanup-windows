using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

/// <summary>Privacidade: browsing traces, recent items, PowerShell history and saved Wi-Fi networks.</summary>
public sealed partial class PrivacyView : UserControl
{
    public PrivacyViewModel Model => PrivacyViewModels.Shared;

    public PrivacyView() => InitializeComponent();

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShowAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!Model.HasLoaded && Model.LoadCommand.CanExecute(null)) Model.LoadCommand.Execute(null);
    }

    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;

    /// <summary>Permanent deletion: always confirmed, listing what is lost; the clean takes exactly the plan shown.</summary>
    private async void OnClean(object sender, RoutedEventArgs e)
    {
        if (!Model.CanClean) return;
        var plan = Model.CurrentPlan();
        if (plan.IsEmpty) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Apagar definitivamente?",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock { Text = plan.ConfirmationText, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            PrimaryButtonText = "Apagar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Model.CleanAsync(plan);
    }
}
