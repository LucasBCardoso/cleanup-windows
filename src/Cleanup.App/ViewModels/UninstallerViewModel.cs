using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Lists;
using Cleanup.Core.Models;
using Cleanup.Core.Uninstaller;
using Cleanup.Platform.Windows.Apps;

namespace Cleanup.App.ViewModels;

/// <summary>One installed app in the Desinstalador's list.</summary>
public sealed partial class AppRow : ObservableObject
{
    public AppRow(InstalledApp app, DateTime? lastUsedUtc)
    {
        App = app;
        LastUsedUtc = lastUsedUtc;
        size = app.Size;
    }

    public InstalledApp App { get; private set; }
    public DateTime? LastUsedUtc { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SizeText))] private long size;

    public void UpdateSize(long bytes)
    {
        App = App with { Size = bytes };
        Size = bytes;
    }

    public string Id => App.Id;
    public string Name => App.Name;
    public string SizeText => Size > 0 ? ByteFormat.Format(Size) : "—";
    public string UsageText => UninstallerViewModel.UsageText(LastUsedUtc, DateTime.UtcNow);
    public string Glyph => App.Kind == AppKind.Store ? "" : "";
    public bool IsStore => App.Kind == AppKind.Store;
    public bool IsSystem => App.IsSystemApp;

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => $"{Name}, {UsageText}, {SizeText}";
}

/// <summary>A selectable list of leftovers or reset items (selection, search and select-all from <see cref="SelectableList"/>).</summary>
public sealed partial class PlanList : SelectableList
{
    public event Action? SelectionChanged;

    public void Set(IEnumerable<(string Title, IEnumerable<ScanItem> Items)> groups, Func<ScanItem, bool> preselect)
    {
        Query = "";
        SetGroups(groups, preselect);
    }

    public void Clear() => SetGroups([], _ => false);

    public void Remove(IReadOnlySet<string> ids) => Drop(ids);

    protected override void UpdateSelection()
    {
        base.UpdateSelection();
        SelectionChanged?.Invoke();
    }
}

/// <summary>A leftover only shown (ProgramData, Program Files, the all-users Start Menu need administrator rights).</summary>
public sealed class ReportRow(ScanItem item)
{
    public ScanItem Item { get; } = item;
    public string Name => Item.Name;
    public string Location => Path.GetDirectoryName(Item.Path) ?? Item.Path;
    public string SizeText => ByteFormat.Format(Item.Size);
}

/// <summary>
/// Desinstalador (+ Redefinir app and the "Apps esquecidos" filter): installed programs and Store apps with size and
/// last use; removing an app runs its own uninstaller, then sends its leftovers to the Recycle Bin.
/// </summary>
public sealed partial class UninstallerViewModel : ObservableObject
{
    /// <summary>The module's view model; Hoje reads <see cref="ForgottenCountAsync"/> from here.</summary>
    public static UninstallerViewModel Shared => shared ??= new();
    private static UninstallerViewModel? shared;

    private readonly AppServices services = AppServices.Shared;
    private readonly WindowsRegistry registry = new();
    private readonly WindowsAppPlatform platform;
    private List<AppRow> allApps = [];
    private Task? loading;

    public static IReadOnlyList<(string Label, UsageFilter Filter)> FilterOptions { get; } =
    [
        ("Todos os apps", UsageFilter.Everything),
        ("Não usados há 3 meses", new UsageFilter.NotUsed(90)),
        ("Não usados há 6 meses", new UsageFilter.NotUsed(180)),
        ("Não usados há 1 ano", new UsageFilter.NotUsed(365)),
        ("Sem registro de uso", new UsageFilter.NoRecord()),
    ];

    public IReadOnlyList<string> FilterLabels { get; } = FilterOptions.Select(o => o.Label).ToList();

    public ObservableCollection<AppRow> VisibleApps { get; } = [];
    public PlanList Leftovers { get; } = new();
    public PlanList ResetPlan { get; } = new();
    public ObservableCollection<ReportRow> ReportOnly { get; } = [];

    [ObservableProperty] private string query = "";
    [ObservableProperty] private int filterIndex;
    [ObservableProperty] private AppRow? chosen;
    [ObservableProperty] private bool isResetMode;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isChoosing;
    [ObservableProperty] private bool isUninstalling;
    [ObservableProperty] private bool isResetting;
    [ObservableProperty] private bool isChosenRunning;
    [ObservableProperty] private bool hasLoaded;
    [ObservableProperty] private Banner? banner;
    /// <summary>Apps not used in 6 months (Hoje's "Apps esquecidos" tile); null until the list has loaded.</summary>
    [ObservableProperty] private int? forgottenCount;

    private UninstallerViewModel()
    {
        platform = new WindowsAppPlatform(services.Environment);
        Leftovers.SelectionChanged += NotifyState;
        ResetPlan.SelectionChanged += NotifyState;
    }

    partial void OnQueryChanged(string value) => Refilter();
    partial void OnFilterIndexChanged(int value) => Refilter();
    partial void OnIsResetModeChanged(bool value)
    {
        NotifyState();
        if (value) _ = RefreshRunningAsync();
    }
    partial void OnIsLoadingChanged(bool value) => NotifyState();
    partial void OnIsChoosingChanged(bool value) => NotifyState();
    partial void OnIsUninstallingChanged(bool value) => NotifyState();
    partial void OnIsResettingChanged(bool value) => NotifyState();
    partial void OnIsChosenRunningChanged(bool value) => NotifyState();
    partial void OnChosenChanged(AppRow? value) => NotifyState();

    // ---- State for the view (plain properties: x:Bind can't nest calls) ----

    public bool IsBusy => IsLoading || IsUninstalling || IsResetting;
    public bool IsWorking => IsUninstalling || IsResetting;
    public bool HasChosen => Chosen is not null;
    public bool NoChosen => Chosen is null;
    public string Subtitle => HasLoaded ? $"{VisibleApps.Count} {(VisibleApps.Count == 1 ? "app" : "apps")}" : "Lendo seus aplicativos…";
    public bool ShowAppsLoading => IsLoading && allApps.Count == 0;
    public bool ShowNoApps => HasLoaded && !IsLoading && VisibleApps.Count == 0;
    public string NoAppsText => Query.Trim().Length > 0 ? $"Nenhum app com “{Query.Trim()}”." : "Nenhum app neste filtro.";

    public string ChosenName => Chosen?.Name ?? "";
    public string ChosenDetails => Chosen is not { } c ? "" : string.Join(" · ", new[]
    {
        c.App.Publisher, c.App.Version is { } v ? "versão " + v : null, c.Size > 0 ? c.SizeText : null, c.App.KindLabel,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string ChosenGlyph => Chosen?.Glyph ?? "";

    private bool ChosenIsSystem => Chosen?.IsSystem == true;
    private bool ChosenIsStore => Chosen?.IsStore == true;

    public bool ShowUninstallPane => HasChosen && !IsResetMode;
    public bool ShowResetPane => HasChosen && IsResetMode;
    public bool ShowChoosing => HasChosen && IsChoosing;

    public string UninstallNote => Chosen is not { } c ? "" :
        c.IsSystem ? $"{c.Name} é um componente do Windows e não pode ser removido." :
        c.IsStore ? "O Windows remove o app e os dados dele desta conta. Nada fica para trás." :
        "O desinstalador do próprio app abre primeiro. Quando o app sair, as sobras marcadas vão para a Lixeira.";
    public bool ShowLeftovers => HasChosen && !IsChoosing && !ChosenIsSystem && !ChosenIsStore && Leftovers.TotalCount > 0;
    public bool ShowNoLeftovers => HasChosen && !IsChoosing && !ChosenIsSystem && !ChosenIsStore && Leftovers.TotalCount == 0;
    public bool ShowReportOnly => HasChosen && !IsChoosing && ReportOnly.Count > 0;

    public string ResetNote => Chosen is not { } c ? "" :
        c.IsSystem ? $"{c.Name} é um componente do Windows e não pode ser redefinido." :
        c.IsStore ? "O Windows apaga os dados do app, como em Configurações › Apps › Opções avançadas › Redefinir. Não dá para desfazer." :
        ResetPlan.TotalCount == 0 ? $"{c.Name} não tem ajustes nem caches para redefinir." :
        "O app volta ao primeiro uso sem ser desinstalado. Ajustes e caches vêm marcados; dados vêm desmarcados porque podem conter seus documentos, contas e bibliotecas.";
    public bool ShowResetList => HasChosen && !IsChoosing && !ChosenIsSystem && !ChosenIsStore && ResetPlan.TotalCount > 0;
    public bool ShowRunningWarning => ShowResetPane && IsChosenRunning && !ChosenIsSystem;
    public string RunningWarning => $"Feche o {ChosenName} para redefinir.";

    public string FooterSizeText => ChosenIsSystem ? "—" : IsResetMode
        ? (ChosenIsStore ? "Dados do app" : ResetPlan.SelectedSizeText)
        : (ChosenIsStore ? (Chosen?.SizeText ?? "") : Leftovers.SelectedSizeText);
    public string ActionText => IsResetMode ? "Redefinir" : "Desinstalar";
    public string ActionGlyph => IsResetMode ? "" : "";
    public bool ShowActionGlyph => !IsWorking;

    public bool CanAct => Chosen is { IsSystem: false } && !IsBusy && !IsChoosing
                          && (!IsResetMode || (!IsChosenRunning && (ChosenIsStore || ResetPlan.SelectedCount > 0)));

    private void NotifyState()
    {
        foreach (var name in (string[])
                 [
                     nameof(IsBusy), nameof(IsWorking), nameof(HasChosen), nameof(NoChosen), nameof(Subtitle), nameof(ShowAppsLoading),
                     nameof(ShowNoApps), nameof(NoAppsText), nameof(ChosenName), nameof(ChosenDetails), nameof(ChosenGlyph),
                     nameof(ShowUninstallPane), nameof(ShowResetPane), nameof(ShowChoosing), nameof(UninstallNote), nameof(ShowLeftovers),
                     nameof(ShowNoLeftovers), nameof(ShowReportOnly), nameof(ResetNote), nameof(ShowResetList), nameof(ShowRunningWarning),
                     nameof(RunningWarning), nameof(FooterSizeText), nameof(ActionText), nameof(ActionGlyph), nameof(ShowActionGlyph), nameof(CanAct),
                 ])
            OnPropertyChanged(name);
        RefreshCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"usado há 3 meses" / "sem registro de uso", like the macOS rows.</summary>
    public static string UsageText(DateTime? lastUsedUtc, DateTime nowUtc)
    {
        if (lastUsedUtc is not { } when) return "sem registro de uso";
        var days = (nowUtc - when).TotalDays;
        if (days < 1) return "usado hoje";
        if (days < 2) return "usado ontem";
        if (days < 30) return $"usado há {(int)days} dias";
        if (days < 365)
        {
            var months = Math.Max(1, (int)(days / 30.4));
            return months == 1 ? "usado há 1 mês" : $"usado há {months} meses";
        }
        var years = (int)(days / 365.25);
        return years <= 1 ? "usado há 1 ano" : $"usado há {years} anos";
    }

    // ---- Loading ----

    public Task EnsureLoadedAsync() => HasLoaded ? Task.CompletedTask : loading ??= LoadAsync();

    /// <summary>Hoje's "Apps esquecidos": apps not used in 6 months (loads the list once if needed).</summary>
    public async Task<int> ForgottenCountAsync()
    {
        await EnsureLoadedAsync();
        return ForgottenCount ?? 0;
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var environment = services.Environment;
            var (apps, usage) = await Task.Run(() =>
            {
                var list = new WindowsAppInventory(environment).Load(measure: false);
                return (list, UserAssistReader.LastUsed(list, environment));
            });
            allApps = apps.Select(a => new AppRow(a, usage.GetValueOrDefault(a.Id))).ToList();
            HasLoaded = true;
            UpdateForgotten();
            Refilter();
            if (Chosen is { } chosen)
            {
                var again = allApps.FirstOrDefault(r => r.Id == chosen.Id);
                if (again is null) ClearChosen();
                else await ChooseAsync(again);
            }
            _ = MeasureAsync(allApps);
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, "Não foi possível ler os aplicativos: " + e.Message); }
        finally
        {
            IsLoading = false;
            loading = null;
        }
    }

    /// <summary>Folder sizes for apps whose installer didn't record one (Store apps always), after the list shows.</summary>
    private static async Task MeasureAsync(List<AppRow> rows)
    {
        var missing = rows.Where(r => r.Size == 0 && r.App.InstallFolder is not null).ToList();
        var apps = missing.Select(r => r.App).ToList();
        await Task.Run(() => WindowsAppInventory.MeasureMissingSizes(apps));
        for (var i = 0; i < missing.Count; i++) missing[i].UpdateSize(apps[i].Size);
    }

    private void UpdateForgotten() =>
        ForgottenCount = AppUsage.UnusedCount(allApps.Select(r => new AppWithUsage(r.App, r.LastUsedUtc)), DateTime.UtcNow);

    private void Refilter()
    {
        var filter = FilterOptions[Math.Clamp(FilterIndex, 0, FilterOptions.Count - 1)].Filter;
        var byId = allApps.ToDictionary(r => r.Id);
        var shown = AppUsage.Filter(allApps.Select(r => new AppWithUsage(r.App, r.LastUsedUtc)), filter, DateTime.UtcNow)
            .Select(a => byId[a.App.Id])
            .Where(r => SearchFilter.Matches(Query, [r.Name, r.App.Publisher ?? ""]))
            .ToList();
        VisibleApps.Clear();
        foreach (var row in shown) VisibleApps.Add(row);
        NotifyState();
    }

    // ---- Choosing an app ----

    private int chooseGeneration;

    public async Task ChooseAsync(AppRow? row)
    {
        if (row is null) { ClearChosen(); return; }
        var generation = ++chooseGeneration;
        Chosen = row;
        IsChoosing = true;
        Leftovers.Clear();
        ResetPlan.Clear();
        ReportOnly.Clear();
        try
        {
            var app = row.App;
            var installed = allApps.Select(r => r.App).ToList();
            var finder = new LeftoverFinder(services.Environment, services.Policy(), registry);
            var (found, plan, running) = await Task.Run(() => (finder.Find(app, installed), AppReset.Plan(app, finder, installed), platform.IsRunning(app)));
            if (generation != chooseGeneration) return; // the user picked another app meanwhile
            var files = found.Removable.Where(i => i.RuleId != LeftoverFinder.RegistryRule).ToList();
            var keys = found.Removable.Where(i => i.RuleId == LeftoverFinder.RegistryRule).ToList();
            Leftovers.Set([($"Arquivos de {app.Name}", files), ("Registro (com cópia .reg)", keys)], _ => true);
            foreach (var item in found.ReportOnly) ReportOnly.Add(new ReportRow(item));
            ResetPlan.Set(Enum.GetValues<ResetKind>().Select(k => (k.Title(), plan.Where(p => p.Kind == k).Select(p => p.Item))),
                item => plan.First(p => p.Id == item.Id).Preselected);
            resetKinds = plan.ToDictionary(p => p.Id, p => p.Kind);
            IsChosenRunning = running;
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally
        {
            if (generation == chooseGeneration) IsChoosing = false;
            NotifyState();
        }
    }

    private Dictionary<string, ResetKind> resetKinds = [];

    private void ClearChosen()
    {
        chooseGeneration++;
        Chosen = null;
        Leftovers.Clear();
        ResetPlan.Clear();
        ReportOnly.Clear();
        IsChoosing = false;
        NotifyState();
    }

    public async Task RefreshRunningAsync()
    {
        if (Chosen is not { } row) return;
        var running = await Task.Run(() => platform.IsRunning(row.App));
        if (Chosen == row) IsChosenRunning = running;
    }

    // ---- Desinstalar ----

    public async Task UninstallAsync()
    {
        if (Chosen is not { } row || !CanAct || IsResetMode) return;
        var app = row.App;
        IsUninstalling = true;
        Banner = null;
        try
        {
            var chosen = Leftovers.SelectedItems();
            var cleaner = new Cleaner(services.Policy(), services.Remover);
            var outcome = await Task.Run(() => AppUninstaller.UninstallAsync(app, chosen, platform, cleaner, registry, RegistryCleanup.BackupDirectory));
            if (outcome.Files is { } files) await services.RecordAsync(files, null, "Desinstalador");
            Banner = UninstallBanner(app, outcome);
            if (outcome.AppRemoved)
            {
                allApps.Remove(row);
                ClearChosen();
                UpdateForgotten();
                Refilter();
            }
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally { IsUninstalling = false; }
    }

    internal static Banner UninstallBanner(InstalledApp app, UninstallOutcome outcome)
    {
        switch (outcome.Run.Exit)
        {
            case UninstallerExit.Cancelled:
                return new Banner(BannerKind.Info, $"Nada foi removido: a permissão de administrador para o desinstalador de {app.Name} não foi dada.");
            case UninstallerExit.Failed when !outcome.AppRemoved:
                return new Banner(BannerKind.Error, $"Não foi possível desinstalar {app.Name}: {outcome.Run.Error}.");
        }
        if (!outcome.AppRemoved)
            return new Banner(BannerKind.Warning,
                $"{app.Name} continua instalado. Se o desinstalador ainda estiver aberto, conclua-o e toque em Atualizar lista. As sobras não foram tocadas.");
        var parts = new List<string> { $"{app.Name} desinstalado." };
        var details = new List<string>();
        if (outcome.Files is { } files)
        {
            if (files.Removed.Count > 0) parts.Add($"{ByteFormat.Format(files.FreedBytes)} de sobras na Lixeira ({files.Removed.Count} {(files.Removed.Count == 1 ? "item" : "itens")}).");
            details.AddRange(files.Failed.Select(f => $"{f.Item.Name}: {f.Reason}"));
        }
        if (outcome.Registry.Removed.Count > 0) parts.Add($"{outcome.Registry.Removed.Count} chave(s) do registro removida(s), com cópia em backups.");
        details.AddRange(outcome.Registry.Failed.Select(f => $"{f.Key}: {f.Reason}"));
        if (details.Count > 0) parts.Add($"{details.Count} não puderam ser removidos.");
        return new Banner(details.Count > 0 ? BannerKind.Warning : BannerKind.Success, string.Join(" ", parts), details);
    }

    // ---- Redefinir ----

    public async Task ResetAsync()
    {
        if (Chosen is not { } row || !CanAct || !IsResetMode) return;
        var app = row.App;
        IsResetting = true;
        Banner = null;
        try
        {
            var chosen = ResetPlan.SelectedItems().Select(i => new ResetItem(i, resetKinds.GetValueOrDefault(i.Id, ResetKind.Data))).ToList();
            var cleaner = new Cleaner(services.Policy(), services.Remover);
            var outcome = await Task.Run(() => AppReset.CleanAsync(chosen, app, platform, cleaner, registry, RegistryCleanup.BackupDirectory));
            if (outcome.Files is { } files) await services.RecordAsync(files, null, $"Redefinir {app.Name}");
            Banner = ResetBanner(app, outcome);
        }
        catch (AppActionException e) { Banner = new Banner(BannerKind.Warning, e.Message); }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally { IsResetting = false; }
        await ChooseAsync(row);
    }

    internal static Banner ResetBanner(InstalledApp app, AppReset.Outcome outcome)
    {
        if (outcome.PackageReset) return new Banner(BannerKind.Success, $"{app.Name} redefinido pelo Windows.");
        var removed = (outcome.Files?.Removed.Count ?? 0) + outcome.Registry.Removed.Count;
        var details = (outcome.Files?.Failed.Select(f => $"{f.Item.Name}: {f.Reason}") ?? [])
            .Concat(outcome.Registry.Failed.Select(f => $"{f.Key}: {f.Reason}")).ToList();
        if (removed == 0) return new Banner(BannerKind.Warning, "Nada foi redefinido.", details);
        var message = $"{app.Name} redefinido: {removed} {(removed == 1 ? "item" : "itens")}.";
        if (outcome.Files is { Removed.Count: > 0 }) message += " Arquivos na Lixeira: dá para desfazer em Hoje › Histórico.";
        if (outcome.Registry.Removed.Count > 0) message += " Ajustes do registro salvos em backups (.reg).";
        if (details.Count > 0) message += $" {details.Count} não puderam ser removidos.";
        return new Banner(details.Count > 0 ? BannerKind.Warning : BannerKind.Success, message, details);
    }

    public static string BackupFolder => RegistryCleanup.BackupDirectory;
}
