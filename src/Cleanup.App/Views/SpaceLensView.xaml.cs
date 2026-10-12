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
        // A new folder starts at the top: the list would otherwise keep the old scroll offset (and look empty).
        Model.Listed += () => { if (List.Items.Count > 0) List.ScrollIntoView(List.Items[0]); };
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

    public string Status(bool measuring, long files, string totalText) =>
        measuring ? (files > 0 ? $"Medindo… {files:N0} arquivos" : "Medindo…") : totalText;

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

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasSelection(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CanRecycle(int count, bool recycling) => count > 0 && !recycling;
    public string SelectionTitle(string text) => text.Length > 0 ? text : "Marque itens para mover para a Lixeira";

    private async void OnRecycle(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LensRow row) await ConfirmAndRecycleAsync([row]);
    }

    private async void OnRecycleSelected(object sender, RoutedEventArgs e) => await ConfirmAndRecycleAsync(Model.SelectedRows);

    /// <summary>Lists what goes (the first few names, the total) before anything moves.</summary>
    private async Task ConfirmAndRecycleAsync(IReadOnlyList<LensRow> rows)
    {
        if (rows.Count == 0) return;
        var total = Cleanup.Core.Models.ByteFormat.Format(rows.Sum(r => r.Entry.Size));
        var names = string.Join(Environment.NewLine, rows.Take(8).Select(r => $"•  {r.Name}  ({r.SizeText})"));
        if (rows.Count > 8) names += $"{Environment.NewLine}… e mais {rows.Count - 8}";
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        var inOneDrive = !string.IsNullOrEmpty(oneDrive)
                         && rows.Any(r => Cleanup.Core.Safety.SafetyPolicy.IsWithin(r.Entry.Path, oneDrive));
        var body = $"{names}{Environment.NewLine}{Environment.NewLine}Total: {total}. Dá para desfazer pelo Histórico enquanto estiver na Lixeira."
                   + (inOneDrive ? $"{Environment.NewLine}{Environment.NewLine}Itens do OneDrive também saem da nuvem e dos seus outros dispositivos (ficam na lixeira do OneDrive)." : "");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = rows.Count == 1 ? $"Mover “{rows[0].Name}” para a Lixeira?" : $"Mover {rows.Count} itens para a Lixeira?",
            Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap } },
            PrimaryButtonText = "Mover para a Lixeira",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Model.RecycleAsync(rows);
    }
}
