using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Developer;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Scanning;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

public enum DeveloperTab { Projects, Caches, Tools }

/// <summary>A selectable list that tells its owner when the selection changes (for the footer and its button).</summary>
public sealed class NotifyingList : SelectableList
{
    public event Action? SelectionChanged;

    public void Show(IEnumerable<(string Title, IEnumerable<ScanItem> Items)> groups, Func<ScanItem, bool> preselect) => SetGroups(groups, preselect);

    public void Remove(IReadOnlySet<string> ids) => Drop(ids);

    protected override void UpdateSelection()
    {
        base.UpdateSelection();
        SelectionChanged?.Invoke();
    }
}

/// <summary>One line of Docker's disk usage, or a WSL disk.</summary>
public sealed record ToolRow(string Title, string Subtitle, string Value, string? Path = null)
{
    public bool CanReveal => Path is not null;
}

/// <summary>
/// Desenvolvedor: stale project build output (Projetos), package-manager and IDE caches plus Android system images
/// (Caches), and Docker / WSL (Ferramentas). Everything removed goes to the Recycle Bin and into the history; Docker
/// is pruned by its own CLI (build cache and dangling images, never volumes).
/// </summary>
public sealed partial class DeveloperViewModel : ObservableObject
{
    private sealed class Preferences
    {
        public List<string> Roots { get; set; } = [];
    }

    private const string StoreName = "project-roots.json";
    private readonly AppServices services = AppServices.Shared;
    private readonly DeveloperTools tools;
    private IReadOnlyList<ProjectArtifact> artifacts = [];
    private static readonly Dictionary<string, ProjectArtifact> ArtifactsById = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<int?> AgeOptions { get; } = [30, 90, 180, null];
    public IReadOnlyList<string> AgeLabels { get; } = AgeOptions.Select(d => d is { } days ? $"Parado há {days} dias" : "Qualquer data").ToList();

    public NotifyingList Projects { get; } = new();
    public NotifyingList Caches { get; } = new();
    public ObservableCollection<FolderChip> Folders { get; } = [];
    public ObservableCollection<ToolRow> Docker { get; } = [];
    public ObservableCollection<ToolRow> WslDisks { get; } = [];

    [ObservableProperty] private DeveloperTab tab = DeveloperTab.Projects;
    [ObservableProperty] private int ageIndex = 1;
    [ObservableProperty] private bool hasScannedProjects;
    [ObservableProperty] private bool isScanningProjects;
    [ObservableProperty] private bool hasScannedCaches;
    [ObservableProperty] private bool isScanningCaches;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CleanCommand))] private bool isCleaning;
    [ObservableProperty] private bool isLoadingTools;
    [ObservableProperty] private string? busyTool;
    [ObservableProperty] private string? dockerError;
    [ObservableProperty] private Banner? banner;

    public DeveloperViewModel()
    {
        tools = new DeveloperTools(new DeveloperToolRunner(), DeveloperTools.DetectDocker(services.Environment, File.Exists));
        var defaults = ProjectArtifactScanner.DefaultRoots(services.Environment.Home, KnownFolders());
        foreach (var root in defaults) Folders.Add(new FolderChip(Name(root), root, isCustom: false, isOn: true));
        foreach (var root in ModuleStore.Load<Preferences>(StoreName).Roots.Where(r => !defaults.Any(d => PathUtil.SameOrdinalIgnoreCase(d, r))))
            Folders.Add(new FolderChip(Name(root), root, isCustom: true, isOn: true));
        Projects.SelectionChanged += OnSelectionChanged;
        Caches.SelectionChanged += OnSelectionChanged;
        // Launch settings for screenshots (read-only): CLEANUP_DEVELOPER_ROOTS="C:;C:" (not saved),
        // CLEANUP_DEVELOPER_TAB=caches|tools, CLEANUP_AUTOSCAN=1.
        if (Environment.GetEnvironmentVariable("CLEANUP_DEVELOPER_ROOTS") is { Length: > 0 } roots)
        {
            persist = false;
            Folders.Clear();
            foreach (var root in roots.Split(';', StringSplitOptions.RemoveEmptyEntries)) Folders.Add(new FolderChip(Name(root), root, isCustom: false, isOn: true));
        }
        tab = Environment.GetEnvironmentVariable("CLEANUP_DEVELOPER_TAB") switch
        {
            "caches" => DeveloperTab.Caches,
            "tools" => DeveloperTab.Tools,
            _ => DeveloperTab.Projects,
        };
        if (Environment.GetEnvironmentVariable("CLEANUP_AUTOSCAN") == "1" && ScanCommand.CanExecute(null)) ScanCommand.Execute(null);
    }

    private readonly bool persist = true;

    private static IEnumerable<string> KnownFolders() =>
        new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0);

    private static string Name(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;

    public bool HasDocker => tools.DockerPath is not null;
    public bool ShowProjects => Tab == DeveloperTab.Projects;
    public bool ShowCaches => Tab == DeveloperTab.Caches;
    public bool ShowTools => Tab == DeveloperTab.Tools;
    public NotifyingList CurrentList => Tab == DeveloperTab.Caches ? Caches : Projects;
    public bool IsScanning => Tab == DeveloperTab.Caches ? IsScanningCaches : IsScanningProjects;
    public bool HasScanned => Tab == DeveloperTab.Caches ? HasScannedCaches : HasScannedProjects;
    public bool ShowFooter => !ShowTools && HasScanned && !IsScanning;
    public bool ShowProjectsIdle => ShowProjects && !HasScannedProjects && !IsScanningProjects;
    public bool ShowProjectsProgress => ShowProjects && IsScanningProjects;
    public bool ShowProjectsList => ShowProjects && HasScannedProjects && !IsScanningProjects && Projects.TotalCount > 0;
    public bool ShowProjectsEmpty => ShowProjects && HasScannedProjects && !IsScanningProjects && Projects.TotalCount == 0;
    public bool ShowCachesIdle => ShowCaches && !HasScannedCaches && !IsScanningCaches;
    public bool ShowCachesProgress => ShowCaches && IsScanningCaches;
    public bool ShowCachesList => ShowCaches && HasScannedCaches && !IsScanningCaches && Caches.TotalCount > 0;
    public bool ShowCachesEmpty => ShowCaches && HasScannedCaches && !IsScanningCaches && Caches.TotalCount == 0;
    public bool ShowDockerError => DockerError is not null;
    public bool ShowDockerActions => HasDocker && DockerError is null && !IsLoadingTools;
    public bool ShowNoWslDisks => WslDisks.Count == 0 && !IsLoadingTools && HasDocker;
    public bool ShowNoTools => !HasDocker && WslDisks.Count == 0 && !IsLoadingTools;
    public bool ToolsIdle => BusyTool is null;

    public string Subtitle => Tab switch
    {
        DeveloperTab.Projects when HasScannedProjects => $"{Projects.TotalCount} itens · {ByteFormat.Format(Projects.TotalSize)}",
        DeveloperTab.Projects => "Dependências e saídas de build de projetos parados",
        DeveloperTab.Caches when HasScannedCaches => $"{Caches.TotalCount} itens · {ByteFormat.Format(Caches.TotalSize)}",
        DeveloperTab.Caches => "Caches de npm, pip, NuGet, Gradle, IDEs e imagens do emulador Android",
        _ => "Docker e discos do WSL",
    };

    public string ScanTitle => HasScanned ? "Escanear novamente" : "Escanear";
    public string SelectedSizeText => ByteFormat.Format(CurrentList.SelectedSize);
    public string FoldersSummary => Folders.Count == 0 ? "nenhuma pasta de projetos encontrada" : "";

    private void Notify()
    {
        foreach (var name in (string[])[nameof(ShowProjects), nameof(ShowCaches), nameof(ShowTools), nameof(CurrentList), nameof(IsScanning),
                     nameof(HasScanned), nameof(ShowFooter), nameof(ShowProjectsIdle), nameof(ShowProjectsProgress), nameof(ShowProjectsList),
                     nameof(ShowProjectsEmpty), nameof(ShowCachesIdle), nameof(ShowCachesProgress), nameof(ShowCachesList), nameof(ShowCachesEmpty),
                     nameof(ShowDockerError), nameof(ShowDockerActions), nameof(ShowNoWslDisks), nameof(ShowNoTools), nameof(ToolsIdle), nameof(Subtitle), nameof(ScanTitle), nameof(SelectedSizeText),
                     nameof(FoldersSummary)])
            OnPropertyChanged(name);
        ScanCommand.NotifyCanExecuteChanged();
        CleanCommand.NotifyCanExecuteChanged();
    }

    partial void OnTabChanged(DeveloperTab value)
    {
        Notify();
        if (value == DeveloperTab.Tools) _ = LoadToolsAsync();
    }

    partial void OnAgeIndexChanged(int value) => ShowArtifacts();
    partial void OnHasScannedProjectsChanged(bool value) => Notify();
    partial void OnIsScanningProjectsChanged(bool value) => Notify();
    partial void OnHasScannedCachesChanged(bool value) => Notify();
    partial void OnIsScanningCachesChanged(bool value) => Notify();
    partial void OnIsCleaningChanged(bool value) => Notify();
    partial void OnBusyToolChanged(string? value) => Notify();
    partial void OnDockerErrorChanged(string? value) => Notify();
    partial void OnIsLoadingToolsChanged(bool value) => Notify();

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedSizeText));
        CleanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Ecossistema · alterado em … · recriar com …" for a project row.</summary>
    public static string Detail(string id)
    {
        if (!ArtifactsById.TryGetValue(id, out var a)) return "";
        var changed = a.LastActivityUtc is { } d ? $"alterado em {d.ToLocalTime():dd/MM/yyyy}" : null;
        return string.Join(" · ", new[] { a.Ecosystem, changed, "recriar com " + a.RebuildHint }.OfType<string>());
    }

    // MARK: Folders

    public void RemoveFolder(FolderChip chip)
    {
        if (!chip.IsCustom || IsScanningProjects) return;
        Folders.Remove(chip);
        SaveFolders();
    }

    public async Task AddFolderAsync()
    {
        if (IsScanningProjects || await ModuleStore.PickFolderAsync() is not { } path) return;
        if (Folders.Any(f => PathUtil.SameOrdinalIgnoreCase(f.Path, path))) return;
        Folders.Add(new FolderChip(Name(path), path, isCustom: true, isOn: true));
        SaveFolders();
    }

    private void SaveFolders()
    {
        if (!persist) return;
        var error = ModuleStore.Save(StoreName, new Preferences { Roots = Folders.Where(f => f.IsCustom).Select(f => f.Path).ToList() });
        if (error is not null) Banner = new Banner(BannerKind.Error, "Não foi possível salvar as pastas.", [error]);
        OnPropertyChanged(nameof(FoldersSummary));
    }

    // MARK: Scan

    private bool CanScan() => !IsScanning && !IsCleaning && (Tab != DeveloperTab.Projects || Folders.Count > 0);

    [RelayCommand(CanExecute = nameof(CanScan), AllowConcurrentExecutions = true)]
    private async Task ScanAsync()
    {
        if (Tab == DeveloperTab.Caches) await ScanCachesAsync();
        else if (Tab == DeveloperTab.Projects) await ScanProjectsAsync();
    }

    private async Task ScanProjectsAsync()
    {
        if (IsScanningProjects) return;
        IsScanningProjects = true;
        Banner = null;
        try
        {
            var roots = Folders.Select(f => f.Path).ToList();
            var result = await Task.Run(() => new ProjectArtifactScanner(roots).Scan());
            artifacts = result.Artifacts;
            ArtifactsById.Clear();
            foreach (var a in artifacts) ArtifactsById[a.Path.ToLowerInvariant()] = a;
            HasScannedProjects = true;
            ShowArtifacts();
            if (result.UnreadableRoots.Count > 0)
                Banner = new Banner(BannerKind.Info, "Algumas pastas não puderam ser lidas (removidas ou sem permissão).", result.UnreadableRoots);
            else if (result.PartialRoots.Count > 0)
                Banner = new Banner(BannerKind.Info, "Algumas pastas são muito grandes e foram lidas só em parte.", result.PartialRoots);
        }
        catch (Exception e)
        {
            Log.Write("developer scan: " + e);
            Banner = new Banner(BannerKind.Error, "A busca falhou.", [e.Message]);
        }
        finally { IsScanningProjects = false; }
    }

    /// <summary>The artifacts of projects idle for the chosen age, grouped by ecosystem. Nothing is pre-selected; the
    /// selection survives a change of age.</summary>
    private void ShowArtifacts()
    {
        var selected = Projects.AllRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        var days = AgeOptions[Math.Clamp(AgeIndex, 0, AgeOptions.Count - 1)];
        var visible = ProjectArtifactScanner.Stale(artifacts, days, DateTime.UtcNow);
        Projects.Show(visible.GroupBy(a => a.Ecosystem)
            .Select(g => (g.Key, (IEnumerable<ScanItem>)g.OrderByDescending(a => a.Size).Select(a => a.ToScanItem()).ToList()))
            .OrderByDescending(g => g.Item2.Sum(i => i.Size)), i => selected.Contains(i.Id));
        Notify();
    }

    private async Task ScanCachesAsync()
    {
        if (IsScanningCaches) return;
        IsScanningCaches = true;
        Banner = null;
        try
        {
            var engine = new ScanEngine(services.Environment, services.Policy());
            var outcomes = await engine.RunAsync(DeveloperCaches.Rules.Select(r => (IScanTask)new RuleScanTask(r)).ToList());
            var reports = outcomes.Select(o => o.Report).OfType<ScanReport>().ToList();
            var selected = Caches.AllRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            var first = !HasScannedCaches;
            // Like Smart Scan: safe caches are pre-selected, review items (stores projects link to, emulator images) aren't.
            Caches.Show(reports.Where(r => r.Items.Count > 0).Select(r => (r.Title, (IEnumerable<ScanItem>)r.Items)),
                i => first ? i.Risk == RiskLevel.Safe : selected.Contains(i.Id));
            HasScannedCaches = true;
            var failures = outcomes.OfType<ScanOutcome.Failure>().Select(f => $"{f.Title}: {f.Message}").ToList();
            Banner = failures.Count > 0 ? new Banner(BannerKind.Warning, "Alguns caches não puderam ser lidos.", failures)
                : Banner.Skipped(reports.SelectMany(r => r.Skipped));
            Notify();
        }
        catch (Exception e)
        {
            Log.Write("developer caches: " + e);
            Banner = new Banner(BannerKind.Error, "A busca falhou.", [e.Message]);
        }
        finally { IsScanningCaches = false; }
    }

    // MARK: Clean

    private bool CanClean() => !IsCleaning && !IsScanning && Tab != DeveloperTab.Tools && CurrentList.SelectedSize > 0;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var list = CurrentList;
        IsCleaning = true;
        try
        {
            var result = await services.CleanAsync(list.SelectedItems());
            await services.RecordAsync(result, null, "Desenvolvedor");
            var removed = result.Removed.Select(i => i.Id).ToHashSet();
            if (list == Projects) artifacts = artifacts.Where(a => !removed.Contains(a.Path.ToLowerInvariant())).ToList();
            list.Remove(removed);
            Banner = Banner.FromClean(result);
        }
        catch (Exception e)
        {
            Log.Write("developer clean: " + e);
            Banner = new Banner(BannerKind.Error, e.Message);
        }
        finally
        {
            IsCleaning = false;
            Notify();
        }
    }

    public void Ignore(ItemRow row)
    {
        services.Ignore(row.Item.Path);
        CurrentList.Remove(new HashSet<string> { row.Id });
        Notify();
    }

    // MARK: Tools

    public async Task LoadToolsAsync()
    {
        if (IsLoadingTools) return;
        IsLoadingTools = true;
        try
        {
            var env = services.Environment;
            var disks = await Task.Run(() => DeveloperTools.WslDisks(env, WslRegistry.Distributions()));
            WslDisks.Clear();
            foreach (var disk in disks) WslDisks.Add(new ToolRow(disk.Name, disk.Path, ByteFormat.Format(disk.Size), disk.Path));
            if (HasDocker)
            {
                try
                {
                    var usage = await Task.Run(tools.DockerUsage);
                    Docker.Clear();
                    foreach (var u in usage) Docker.Add(new ToolRow(u.Type, $"{u.Count} itens · recuperável {u.Reclaimable}", u.Size));
                    DockerError = null;
                }
                catch (DeveloperToolException e)
                {
                    Docker.Clear();
                    DockerError = e.Message;
                }
            }
        }
        catch (Exception e) { Log.Write("developer tools: " + e); }
        finally { IsLoadingTools = false; }
    }

    public Task PruneDockerBuildCacheAsync() => RunToolAsync("docker-cache", "Cache de build do Docker limpo.", tools.PruneDockerBuildCache);

    public Task PruneDanglingImagesAsync() => RunToolAsync("docker-images", "Imagens do Docker sem uso removidas.", tools.PruneDanglingImages);

    private async Task RunToolAsync(string id, string success, Action action)
    {
        if (BusyTool is not null) return;
        BusyTool = id;
        try
        {
            await Task.Run(action);
            Banner = new Banner(BannerKind.Success, success);
        }
        catch (Exception e) when (e is DeveloperToolException or IOException or System.ComponentModel.Win32Exception)
        {
            Banner = new Banner(BannerKind.Error, "A operação falhou.", [e.Message]);
        }
        finally { BusyTool = null; }
        await LoadToolsAsync();
    }
}
