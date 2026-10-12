using System.Collections.ObjectModel;
using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Cleanup.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Cleanup.App.Views;

public sealed partial class SpaceLensView : UserControl
{
    public SpaceLensViewModel Model => AppViewModels.SpaceLens;

    /// <summary>Folder names for the breadcrumb ("C:", "Users", "me"…), parallel to <see cref="SpaceLensViewModel.Crumbs"/>.</summary>
    public ObservableCollection<string> CrumbNames { get; } = [];

    public SpaceLensView()
    {
        InitializeComponent();
        Model.Crumbs.CollectionChanged += (_, _) =>
        {
            CrumbNames.Clear();
            foreach (var c in Model.Crumbs) CrumbNames.Add(Path.GetFileName(c) is { Length: > 0 } n ? n : c.TrimEnd('\\'));
        };
        Loaded += async (_, _) =>
        {
            // Space Lens starts in the profile folder: the drive root needs admin for much of it.
            if (string.IsNullOrEmpty(Model.CurrentPath)) await Model.OpenHomeAsync();
        };
    }

    public string DriveTitle(DriveRow? drive) => drive is null ? "" : $"Disco {drive.Label}";
    public string DriveSummary(DriveRow? drive) =>
        drive is null ? "" : $"{ByteFormat.Format(drive.Disk.Used)} usados · {ByteFormat.Format(drive.Disk.Free)} livres · {ByteFormat.Format(drive.Disk.Total)}";
    public double DriveUsed(DriveRow? drive) => drive?.UsedFraction ?? 0;
    public Visibility HasBanner(Banner? banner) => banner is null ? Visibility.Collapsed : Visibility.Visible;

    public string Status(bool measuring, int measured, int total, string totalText) =>
        measuring ? $"Medindo {measured} de {total}…" : totalText;

    private void OnBannerDismissed(object? sender, EventArgs e) => Model.Banner = null;

    private async void OnCrumb(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index < Model.Crumbs.Count) await Model.OpenAsync(Model.Crumbs[args.Index]);
    }

    private async void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LensRow { CanOpen: true } row) await Model.OpenAsync(row.Entry.Path);
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LensRow { CanOpen: true } row) await Model.OpenAsync(row.Entry.Path);
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LensRow row) AppServices.Reveal(row.Entry.Path);
    }

    private async void OnRecycle(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LensRow row) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Mover “{row.Name}” para a Lixeira?",
            Content = $"{row.SizeText}. Dá para desfazer pelo Histórico enquanto estiver na Lixeira.",
            PrimaryButtonText = "Mover para a Lixeira",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Model.RecycleAsync(row);
    }
}
