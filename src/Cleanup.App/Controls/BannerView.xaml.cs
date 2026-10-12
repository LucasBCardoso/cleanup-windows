using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Controls;

public sealed partial class BannerView : UserControl
{
    public static readonly DependencyProperty BannerProperty =
        DependencyProperty.Register(nameof(Banner), typeof(Banner), typeof(BannerView), new PropertyMetadata(null, (d, _) => ((BannerView)d).Render()));

    public Banner? Banner
    {
        get => (Banner?)GetValue(BannerProperty);
        set => SetValue(BannerProperty, value);
    }

    /// <summary>Raised when the user closes the banner; the page clears its view model's banner.</summary>
    public event EventHandler? Dismissed;

    public BannerView() => InitializeComponent();

    private void Render()
    {
        if (Banner is not { } banner)
        {
            Root.Visibility = Visibility.Collapsed;
            return;
        }
        Root.Visibility = Visibility.Visible;
        OkIcon.Visibility = Show(banner.Kind == BannerKind.Success);
        InfoIcon.Visibility = Show(banner.Kind == BannerKind.Info);
        WarnIcon.Visibility = Show(banner.Kind == BannerKind.Warning);
        ErrorIcon.Visibility = Show(banner.Kind == BannerKind.Error);
        Message.Text = banner.Message;
        Details.Text = banner.DetailsText;
        DetailsButton.Visibility = banner.HasDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Banner = null;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
