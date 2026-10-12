using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Safety;
using Cleanup.Core.Updater;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

/// <summary>The Apps tab's view models (Atualizador, Extensões): one instance each for the app's lifetime.</summary>
public static class AppsViewModels
{
    public static UpdaterViewModel Updater { get; } = new();
    public static ExtensionsViewModel Extensions { get; } = new();
}

public enum UpdaterPhase { Idle, Checking, Results, WingetMissing }

/// <summary>One available update.</summary>
public sealed partial class UpdateRow(AvailableUpdate update) : SelectableRow
{
    public AvailableUpdate Update { get; } = update;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Detail))] private bool isInstalling;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? error;

    public override string Id => Update.Id;
    /// <summary>Store rows can be ticked (they open the Store); a truncated id can't be installed from here.</summary>
    public override bool CanSelect => Update.CanInstallWithWinget || IsStore;
    public override IEnumerable<string> SearchFields => [Update.Name, Update.AppId, Update.Source.DisplayName()];

    public string Name => Update.Name;
    public bool IsStore => Update.Source == UpdateSourceKind.MicrosoftStore;
    public bool IsWinget => !IsStore;
    public bool IsExplicit => Update.RequiresExplicitTargeting;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string SourceText => Update.Source.DisplayName();
    public string CurrentVersion => WingetCommands.IsUnknownVersion(Update.CurrentVersion) ? "desconhecida" : Update.CurrentVersion;
    public string NewVersion => Update.NewVersion;

    public string Detail => IsInstalling ? "Atualizando…"
        : IsStore ? "Microsoft Store · abre a loja"
        : Update.IsIdTruncated ? $"winget · {Update.AppId} — id incompleto: atualize pelo Terminal com winget upgrade"
        : $"winget · {Update.AppId}";
}

/// <summary>An entry of the ignore list, with "Deixar de ignorar".</summary>
public sealed class IgnoredRow(IgnoredUpdate entry)
{
    public IgnoredUpdate Entry { get; } = entry;
    public string AppId => Entry.AppId;
    public string VersionText => Entry.Version is { } v ? "versão " + v : "todas as versões";
}

/// <summary>
/// Atualizador: <c>winget upgrade</c> results, select-all (Store items left out: they open the Store), search,
/// "Ignorar esta versão / este app", installs one at a time with a spinner per row. Mirrors the Swift UpdaterModel.
/// </summary>
public sealed partial class UpdaterViewModel : RowList<UpdateRow>
{
    /// <summary>Updates found by the last check (null before any check), for Hoje's Atualizações tile.</summary>
    public static int? UpdatesFound { get; private set; }
    public static event Action? UpdatesFoundChanged;

    private readonly IToolRunner runner = new ProcessToolRunner();
    private WingetInstaller? installer;
    private string? wingetPath;
    private IgnoredUpdates ignored = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsChecking), nameof(HasResults), nameof(IsWingetMissing))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand))]
    private UpdaterPhase phase = UpdaterPhase.Idle;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand))] private bool isInstalling;
    [ObservableProperty] private Banner? banner;
    [ObservableProperty] private string subtitle = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowIgnoredText))] private bool showIgnored;

    public ObservableCollection<UpdateFailure> Failures { get; } = [];
    public ObservableCollection<IgnoredRow> IgnoredEntries { get; } = [];

    public bool IsIdle => Phase == UpdaterPhase.Idle;
    public bool IsChecking => Phase == UpdaterPhase.Checking;
    public bool HasResults => Phase == UpdaterPhase.Results;
    public bool IsWingetMissing => Phase == UpdaterPhase.WingetMissing;
    public bool HasFailures => Failures.Count > 0;
    public bool HasIgnored => IgnoredEntries.Count > 0;
    public string ShowIgnoredText => ShowIgnored ? "Ocultar" : "Mostrar";

    public UpdaterViewModel()
    {
        try { ignored = IgnoredUpdates.Load(); }
        catch (IgnoreListException e) { banner = new Banner(BannerKind.Error, e.Message); }
        RefreshIgnored();
    }

    protected override bool InSelectAll(UpdateRow row) => row.CanSelect && !row.IsStore && !row.IsExplicit;

    protected override void SelectionDidChange() => InstallCommand.NotifyCanExecuteChanged();

    private bool CanCheck() => !IsChecking && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (IsChecking || IsInstalling) return;
        wingetPath = WingetLocator.Find();
        if (wingetPath is null)
        {
            Phase = UpdaterPhase.WingetMissing;
            return;
        }
        installer ??= new WingetInstaller(runner, wingetPath);
        Phase = UpdaterPhase.Checking;
        Banner = null;
        try
        {
            var checker = new UpdateChecker([new WingetSource(runner, wingetPath)], ignored);
            var report = await Task.Run(() => checker.CheckAsync());
            var rows = report.Updates.Select(u => new UpdateRow(u) { IsSelected = u.CanInstallWithWinget && !u.RequiresExplicitTargeting }).ToList();
            SetRows([("Atualizações", null, r => $"{r.Count}", rows)]);
            Failures.Clear();
            foreach (var f in report.Failures) Failures.Add(f);
            OnPropertyChanged(nameof(HasFailures));
            Subtitle = SubtitleText(rows.Count, report.IgnoredCount);
            Publish(rows.Count);
        }
        catch (Exception e)
        {
            SetRows([]);
            Banner = new Banner(BannerKind.Error, "Não foi possível verificar: " + e.Message);
        }
        Phase = UpdaterPhase.Results;
    }

    private static string SubtitleText(int count, int ignoredCount)
    {
        var text = count == 1 ? "1 atualização" : $"{count} atualizações";
        if (ignoredCount > 0) text += $" · {ignoredCount} ignorada{(ignoredCount == 1 ? "" : "s")}";
        return $"{text} · verificado às {DateTime.Now:HH:mm}";
    }

    private static void Publish(int count)
    {
        UpdatesFound = count;
        UpdatesFoundChanged?.Invoke();
    }

    private bool CanInstall() => HasResults && !IsInstalling && SelectedCount > 0;

    /// <summary>Selected winget updates one after the other (never two installers at once); Store rows open the Store once.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (IsInstalling || installer is null) return;
        var targets = SelectedRows();
        if (targets.Count == 0) return;
        IsInstalling = true;
        Banner = null;
        var done = new List<string>();
        var failed = new List<string>();
        var installed = new HashSet<string>();
        try
        {
            foreach (var row in targets.Where(r => !r.IsStore))
            {
                row.Error = null;
                row.IsInstalling = true;
                var result = await installer.InstallAsync(row.Update);
                row.IsInstalling = false;
                if (result.Succeeded)
                {
                    done.Add(row.Name);
                    installed.Add(row.Id);
                }
                else
                {
                    row.Error = result.Error;
                    failed.Add($"{row.Name}: {result.Error}");
                }
            }
            var openedStore = targets.Any(r => r.IsStore);
            if (openedStore) AppServices.Open(WingetCommands.StoreUpdatesUri);
            if (installed.Count > 0)
            {
                DropRows(installed);
                Publish(RowCount);
                Subtitle = SubtitleText(RowCount, 0);
            }
            Banner = failed.Count > 0
                ? new Banner(BannerKind.Error, $"{failed.Count} atualização(ões) falharam." + (done.Count > 0 ? $" Atualizado(s): {string.Join(", ", done)}." : ""), failed)
                : done.Count > 0 ? new Banner(BannerKind.Success, $"Atualizado(s): {string.Join(", ", done)}.")
                : openedStore ? new Banner(BannerKind.Info, "A Microsoft Store foi aberta em Downloads e atualizações.") : null;
        }
        finally { IsInstalling = false; }
    }

    [RelayCommand]
    private void OpenStore() => AppServices.Open(WingetCommands.StoreUpdatesUri);

    [RelayCommand]
    private void GetAppInstaller() => AppServices.Open(WingetCommands.AppInstallerStoreUri);

    [RelayCommand]
    private void ToggleIgnored() => ShowIgnored = !ShowIgnored;

    public void Ignore(UpdateRow row, bool wholeApp)
    {
        if (IsInstalling) return;
        var next = ignored.Adding(row.Update.AppId, wholeApp ? null : row.Update.NewVersion);
        if (!Save(next)) return;
        DropRows(AllItems.Where(r => next.IsIgnored(r.Update)).Select(r => r.Id).ToHashSet());
        Publish(RowCount);
        Banner = new Banner(BannerKind.Info, wholeApp ? $"{row.Name} não será mais verificado." : $"A versão {row.NewVersion} de {row.Name} foi ignorada.");
    }

    public void Unignore(IgnoredRow row)
    {
        if (!Save(ignored.Removing(row.Entry))) return;
        Banner = new Banner(BannerKind.Info, $"Verifique novamente para ver as atualizações de {row.AppId}.");
    }

    private bool Save(IgnoredUpdates next)
    {
        try
        {
            next.Save();
            ignored = next;
            RefreshIgnored();
            return true;
        }
        catch (IgnoreListException e)
        {
            Banner = new Banner(BannerKind.Error, e.Message);
            return false;
        }
    }

    private void RefreshIgnored()
    {
        IgnoredEntries.Clear();
        foreach (var entry in ignored.Entries.OrderBy(e => e.AppId, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Version ?? "", StringComparer.Ordinal))
            IgnoredEntries.Add(new IgnoredRow(entry));
        OnPropertyChanged(nameof(HasIgnored));
    }
}
