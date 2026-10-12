using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Duplicates;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

/// <summary>The view models of the Duplicados and Desenvolvedor modules (they outlive their views).</summary>
public static class DuplicatesDeveloperViewModels
{
    public static DuplicatesViewModel Duplicates { get; } = new();
    public static DeveloperViewModel Developer { get; } = new();
}

public enum DuplicatesTab { Identical, Similar }

public enum DuplicatesPhase { Idle, Reading, Comparing, Photos, Done }

/// <summary>
/// A list of files in groups where one copy always stays: "Selecionar tudo" leaves out each group's kept copy
/// (the keep rule's pick, or the best photo).
/// </summary>
public sealed class KeepOneList : SelectableList
{
    /// <summary>Row ids (lower-case paths) of the copies each group keeps.</summary>
    public HashSet<string> Keepers { get; private set; } = [];

    public event Action? SelectionChanged;

    protected override IEnumerable<ItemRow> SelectableRows => ShownRows.Where(r => !Keepers.Contains(r.Id));

    public void Show(IEnumerable<(string Title, IEnumerable<ScanItem> Items)> groups, IEnumerable<string> keeperPaths, Func<ScanItem, bool> preselect)
    {
        Keepers = keeperPaths.Select(p => p.ToLowerInvariant()).ToHashSet();
        SetGroups(groups, preselect);
    }

    public HashSet<string> SelectedPaths() => AllRows.Where(r => r.IsSelected).Select(r => r.Item.Path).ToHashSet();

    public void Select(IEnumerable<string> paths, bool selected)
    {
        var ids = paths.Select(p => p.ToLowerInvariant()).ToHashSet();
        foreach (var row in AllRows.Where(r => ids.Contains(r.Id))) row.IsSelected = selected;
    }

    protected override void UpdateSelection()
    {
        base.UpdateSelection();
        SelectionChanged?.Invoke();
    }
}

/// <summary>Duplicados: identical files (staged hashes) and similar photos (perceptual hashes) in folders the user picks.</summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private sealed class Preferences
    {
        public List<string> Roots { get; set; } = [];
        public long MinSize { get; set; } = 1_048_576;
        public SimilarityLevel Similarity { get; set; } = SimilarityLevel.Normal;
    }

    private const string StoreName = "duplicates.json";
    private readonly AppServices services = AppServices.Shared;
    private readonly FileIdentity fileId = RecycleBin.FileId;
    private readonly WindowsImageFingerprinter fingerprinter = new();
    private CancellationTokenSource? cancellation;
    private IReadOnlyList<DuplicateGroup> groups = [];
    private IReadOnlyList<SimilarGroup> similar = [];
    private IReadOnlyDictionary<string, string> bestPhotoIds = new Dictionary<string, string>();

    public static IReadOnlyList<long> SizeOptions { get; } = [100 * 1024, 1_048_576, 10 * 1_048_576];
    public IReadOnlyList<string> SizeLabels { get; } = SizeOptions.Select(SizeLabel).ToList();
    public IReadOnlyList<string> SimilarityLabels { get; } = SimilarityLevels.All.Select(l => l.Title()).ToList();
    public IReadOnlyList<string> KeepRuleLabels { get; } = KeepRules.All.Select(r => r.DisplayName()).ToList();

    public KeepOneList Identical { get; } = new();
    public KeepOneList Similar { get; } = new();
    public ObservableCollection<FolderChip> Folders { get; } = [];

    [ObservableProperty] private DuplicatesTab tab = DuplicatesTab.Identical;
    [ObservableProperty] private DuplicatesPhase phase = DuplicatesPhase.Idle;
    [ObservableProperty] private int sizeIndex;
    [ObservableProperty] private int similarityIndex;
    [ObservableProperty] private int keepRuleIndex;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(ScanCommand))] private bool isRemoving;
    [ObservableProperty] private bool photosCancelled;
    [ObservableProperty] private int skippedImages;
    [ObservableProperty] private Banner? banner;

    public DuplicatesViewModel()
    {
        var preferences = ModuleStore.Load<Preferences>(StoreName);
        sizeIndex = Math.Max(0, SizeOptions.ToList().IndexOf(preferences.MinSize));
        similarityIndex = Math.Clamp((int)preferences.Similarity, 0, SimilarityLevels.All.Count - 1);
        var home = services.Environment.Home;
        (string Title, string Path)[] picks =
        [
            ("Downloads", Path.Combine(home, "Downloads")),
            ("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Área de Trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        ];
        var saved = preferences.Roots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, path) in picks.Where(p => p.Path.Length > 0))
            Folders.Add(new FolderChip(title, path, isCustom: false, isOn: saved.Contains(path)));
        foreach (var path in preferences.Roots.Where(r => !picks.Any(p => PathUtil.SameOrdinalIgnoreCase(p.Path, r))))
            Folders.Add(new FolderChip(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path, path, isCustom: true, isOn: true));
        Identical.SelectionChanged += OnSelectionChanged;
        Similar.SelectionChanged += OnSelectionChanged;
        // Launch settings for screenshots, like the macOS launch defaults (read-only: they only pick folders and search):
        // CLEANUP_DUPLICATES_ROOTS="C:;C:" (not saved), CLEANUP_DUPLICATES_TAB=similar, CLEANUP_AUTOSCAN=1.
        if (Environment.GetEnvironmentVariable("CLEANUP_DUPLICATES_ROOTS") is { Length: > 0 } roots)
        {
            persist = false;
            Folders.Clear();
            foreach (var root in roots.Split(';', StringSplitOptions.RemoveEmptyEntries))
                Folders.Add(new FolderChip(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), root, isCustom: true, isOn: true));
        }
        if (Environment.GetEnvironmentVariable("CLEANUP_DUPLICATES_TAB") == "similar") tab = DuplicatesTab.Similar;
        if (Environment.GetEnvironmentVariable("CLEANUP_AUTOSCAN") == "1" && ScanCommand.CanExecute(null)) ScanCommand.Execute(null);
    }

    private readonly bool persist = true;

    public static string SizeLabel(long bytes) => "Acima de " + ByteFormat.Format(bytes);

    private long MinSize => SizeOptions[Math.Clamp(SizeIndex, 0, SizeOptions.Count - 1)];
    private SimilarityLevel Similarity => SimilarityLevels.All[Math.Clamp(SimilarityIndex, 0, SimilarityLevels.All.Count - 1)];
    private KeepRule KeepRule => KeepRules.All[Math.Clamp(KeepRuleIndex, 0, KeepRules.All.Count - 1)];
    public List<string> Roots => Folders.Where(f => f.IsOn).Select(f => f.Path).ToList();

    public bool IsScanning => Phase is DuplicatesPhase.Reading or DuplicatesPhase.Comparing or DuplicatesPhase.Photos;
    /// <summary>Identical results are ready from the photo phase on; similar ones only when done.</summary>
    public bool HasResults => Tab == DuplicatesTab.Identical ? Phase is DuplicatesPhase.Photos or DuplicatesPhase.Done : Phase == DuplicatesPhase.Done;
    public KeepOneList CurrentList => Tab == DuplicatesTab.Identical ? Identical : Similar;
    public long Wasted => groups.Sum(g => g.Wasted);

    public bool IsIdenticalTab => Tab == DuplicatesTab.Identical;
    public bool IsSimilarTab => Tab == DuplicatesTab.Similar;
    public bool ShowIdle => Phase == DuplicatesPhase.Idle;
    public bool ShowProgress => IsScanning && !HasResults;
    public bool ShowIdenticalList => Tab == DuplicatesTab.Identical && HasResults && groups.Count > 0;
    public bool ShowIdenticalEmpty => Tab == DuplicatesTab.Identical && HasResults && groups.Count == 0;
    public bool ShowSimilarList => Tab == DuplicatesTab.Similar && HasResults && !PhotosCancelled && similar.Count > 0;
    public bool ShowSimilarEmpty => Tab == DuplicatesTab.Similar && HasResults && !PhotosCancelled && similar.Count == 0;
    public bool ShowPhotosCancelled => Tab == DuplicatesTab.Similar && HasResults && PhotosCancelled;
    public bool ShowIdenticalBar => ShowIdenticalList && !IsRemoving;
    public bool ShowSimilarBar => ShowSimilarList && !IsRemoving;
    public bool ShowSimilarity => Tab == DuplicatesTab.Similar;
    public bool ShowKeepRule => Tab == DuplicatesTab.Identical;
    public bool CanEditFolders => !IsScanning;

    public string ScanTitle => HasResults ? "Procurar de novo" : "Procurar";
    public string IdleMessage => Roots.Count == 0
        ? "Escolha as pastas acima. Arquivos só na nuvem (OneDrive) não são baixados nem comparados."
        : "Procura arquivos idênticos e fotos parecidas. Uma cópia de cada grupo sempre fica.";
    public string PhaseTitle => Phase switch
    {
        DuplicatesPhase.Reading => "Lendo as pastas…",
        DuplicatesPhase.Comparing => "Comparando arquivos…",
        DuplicatesPhase.Photos => "Analisando fotos…",
        _ => "",
    };
    public string IdenticalEmptyMessage => $"Nada idêntico acima de {ByteFormat.Format(MinSize)} nessas pastas.";

    public string Subtitle
    {
        get
        {
            if (!HasResults) return "Arquivos idênticos e fotos parecidas nas pastas que você escolher";
            if (Tab == DuplicatesTab.Identical) return $"{groups.Count} grupos · {ByteFormat.Format(Wasted)} repetidos";
            var skipped = SkippedImages > 0 ? $" · {SkippedImages} imagens ilegíveis" : "";
            return $"{similar.Count} grupos de fotos parecidas{skipped}";
        }
    }

    public long SelectedSize => CurrentList.SelectedSize;
    public string SelectedSizeText => ByteFormat.Format(SelectedSize);

    private void Notify()
    {
        foreach (var name in (string[])[nameof(IsScanning), nameof(HasResults), nameof(IsIdenticalTab), nameof(IsSimilarTab), nameof(CurrentList), nameof(ShowIdle), nameof(ShowProgress),
                     nameof(ShowIdenticalList), nameof(ShowIdenticalEmpty), nameof(ShowSimilarList), nameof(ShowSimilarEmpty),
                     nameof(ShowPhotosCancelled), nameof(ShowIdenticalBar), nameof(ShowSimilarBar), nameof(ShowSimilarity), nameof(ShowKeepRule),
                     nameof(CanEditFolders), nameof(ScanTitle), nameof(IdleMessage), nameof(PhaseTitle), nameof(IdenticalEmptyMessage),
                     nameof(Subtitle), nameof(SelectedSize), nameof(SelectedSizeText)])
            OnPropertyChanged(name);
        ScanCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    partial void OnTabChanged(DuplicatesTab value) => Notify();
    partial void OnPhaseChanged(DuplicatesPhase value) => Notify();
    partial void OnPhotosCancelledChanged(bool value) => Notify();
    partial void OnIsRemovingChanged(bool value) => Notify();
    partial void OnSkippedImagesChanged(int value) => Notify();
    partial void OnSizeIndexChanged(int value) { SavePreferences(); Notify(); }
    partial void OnSimilarityIndexChanged(int value) => SavePreferences();
    partial void OnKeepRuleIndexChanged(int value) => ShowIdentical(preserveSimilar: true, applyRule: true);

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedSize));
        OnPropertyChanged(nameof(SelectedSizeText));
        RemoveCommand.NotifyCanExecuteChanged();
    }

    // MARK: Folders

    /// <summary>After a chip was toggled: a custom folder turned off leaves the bar.</summary>
    public void FolderToggled(FolderChip chip)
    {
        if (chip.IsCustom && !chip.IsOn) Folders.Remove(chip);
        SavePreferences();
        Notify();
    }

    public async Task AddFolderAsync()
    {
        if (IsScanning || await ModuleStore.PickFolderAsync() is not { } path) return;
        if (Folders.FirstOrDefault(f => PathUtil.SameOrdinalIgnoreCase(f.Path, path)) is { } existing) existing.IsOn = true;
        else Folders.Add(new FolderChip(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path, path, isCustom: true, isOn: true));
        SavePreferences();
        Notify();
    }

    private void SavePreferences()
    {
        if (!persist) return;
        var error = ModuleStore.Save(StoreName, new Preferences { Roots = Roots, MinSize = MinSize, Similarity = Similarity });
        if (error is not null) Banner = new Banner(BannerKind.Error, "Não foi possível salvar as pastas.", [error]);
    }

    // MARK: Scan

    private bool CanScan() => !IsScanning && !IsRemoving && Roots.Count > 0;

    /// <summary>One walk serves both tabs; identical results appear as soon as they're ready, photos follow.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        cancellation?.Cancel();
        var cts = new CancellationTokenSource();
        cancellation = cts;
        var token = cts.Token;
        Banner = null;
        groups = [];
        similar = [];
        bestPhotoIds = new Dictionary<string, string>();
        Identical.Show([], [], _ => false);
        Similar.Show([], [], _ => false);
        PhotosCancelled = false;
        SkippedImages = 0;
        Phase = DuplicatesPhase.Reading;
        var roots = Roots;
        var minSize = MinSize;
        var maxDistance = Similarity.MaxDistance();
        try
        {
            var walk = await Task.Run(() => FileWalker.Files(roots, new WalkOptions(Math.Min(minSize, SimilarPhotoFinder.MinimumSize)), fileId, token));
            if (token.IsCancellationRequested) return;
            Phase = DuplicatesPhase.Comparing;
            var large = walk.Files.Where(f => f.Size >= minSize).ToList();
            var found = await Task.Run(() => DuplicateFinder.Groups(large, token));
            if (token.IsCancellationRequested) return;
            groups = found;
            ShowIdentical(preserveSimilar: false, applyRule: true);
            Banner = WalkBanner(walk);
            Phase = DuplicatesPhase.Photos;

            var images = walk.Files.Where(f => f.Size >= SimilarPhotoFinder.MinimumSize
                                               && SimilarPhotoFinder.Extensions.Contains(Path.GetExtension(f.Path).TrimStart('.').ToLowerInvariant())).ToList();
            var exact = found.SelectMany(g => g.Files.Select(f => f.Path)).ToHashSet();
            var photos = await Task.Run(() => SimilarPhotoFinder.Groups(images, exact, maxDistance, fingerprinter, token));
            if (token.IsCancellationRequested) return;
            similar = photos.Groups;
            bestPhotoIds = photos.BestImageIds;
            SkippedImages = photos.Skipped;
            ShowSimilar();
            Phase = DuplicatesPhase.Done;
            if (photos.Partial && Banner is null)
                Banner = new Banner(BannerKind.Info, $"Só as primeiras {SimilarPhotoFinder.ImageLimit} imagens foram comparadas.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Write("duplicates: " + e);
            Banner = new Banner(BannerKind.Error, "A busca falhou.", [e.Message]);
            Phase = groups.Count > 0 ? DuplicatesPhase.Done : DuplicatesPhase.Idle;
        }
    }

    private static Banner? WalkBanner(WalkResult walk)
    {
        if (walk.UnreadableRoots.Count > 0)
            return new Banner(BannerKind.Error, $"Sem acesso a {walk.UnreadableRoots.Count} pasta(s): não existem mais ou o Windows negou a leitura.", walk.UnreadableRoots);
        if (walk.PartialRoots.Count > 0)
            return new Banner(BannerKind.Info, "Algumas pastas são muito grandes e foram lidas só em parte.", walk.PartialRoots);
        return null;
    }

    private bool CanCancel() => IsScanning;

    /// <summary>Stops the work in progress. Identical results already shown stay.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        cancellation?.Cancel();
        cancellation = null;
        if (Phase == DuplicatesPhase.Photos)
        {
            PhotosCancelled = true;
            Phase = DuplicatesPhase.Done;
        }
        else Phase = groups.Count == 0 ? DuplicatesPhase.Idle : DuplicatesPhase.Done;
    }

    // MARK: Lists

    private static ScanItem Item(WalkedFile file) => new()
    {
        Path = file.Path, Size = file.Size, RuleId = "duplicate", Category = ScanCategory.LargeOldFiles, Risk = RiskLevel.Review,
        LastWriteUtc = file.ModifiedUtc,
    };

    /// <summary>Identical groups: every copy except the keeper is selected (when <paramref name="applyRule"/>).</summary>
    private void ShowIdentical(bool preserveSimilar, bool applyRule)
    {
        var home = services.Environment.Home;
        var rule = KeepRule;
        var selected = applyRule ? KeepRules.AutoSelect(groups, rule, home) : Identical.SelectedPaths();
        var keepers = groups.Select(g => rule.Keeper(g, home)?.Path).OfType<string>().ToList();
        Identical.Show(groups.Select(g => (Title(g), g.Files.Select(Item))), keepers, i => selected.Contains(i.Path));
        if (!preserveSimilar) Similar.Show([], [], _ => false);
        Notify();
    }

    private static string Title(DuplicateGroup g) =>
        $"{g.Files.Count} cópias de {ByteFormat.Format(g.Size)} · {ByteFormat.Format(g.Wasted)} repetidos";

    /// <summary>Similar photos are never pre-selected.</summary>
    private void ShowSimilar()
    {
        var selected = Similar.SelectedPaths();
        Similar.Show(similar.Select(g => ($"{g.Images.Count} fotos parecidas", g.Images.Select(Item))), BestPhotos(), i => selected.Contains(i.Path));
        Notify();
    }

    /// <summary>The best photo of each group; if it was removed, the first one stays rather than the whole group.</summary>
    private IEnumerable<string> BestPhotos() => similar.Select(g =>
        bestPhotoIds.TryGetValue(g.Id, out var best) && g.Images.Any(i => i.Id == best) ? best : g.Images[0].Path);

    public bool IsKeeper(ItemRow row) => (Tab == DuplicatesTab.Identical ? Identical : Similar).Keepers.Contains(row.Id);

    /// <summary>"Manter a melhor": selects every photo of the group except the best one.</summary>
    public void KeepBest(ItemGroup shown)
    {
        var ids = shown.Select(r => r.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (similar.FirstOrDefault(g => g.Images.Any(i => ids.Contains(i.Path))) is not { } group) return;
        var best = bestPhotoIds.GetValueOrDefault(group.Id) ?? group.Images[0].Path;
        Similar.Select(group.Images.Select(i => i.Path).Where(p => p != best), true);
        Similar.Select([best], false);
    }

    // MARK: Remove

    private bool CanRemove() => HasResults && !IsRemoving && !IsScanning && CurrentList.SelectedSize > 0;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        // Only the visible tab, never the last copy, and only files that are still what was scanned.
        var list = CurrentList;
        var chosen = list.SelectedPaths();
        var home = services.Environment.Home;
        var rule = KeepRule;
        IsRemoving = true;
        try
        {
            HashSet<string> safe;
            List<WalkedFile> visible;
            if (Tab == DuplicatesTab.Identical)
            {
                var current = groups;
                visible = current.SelectMany(g => g.Files).ToList();
                safe = await Task.Run(() => DuplicateVerifier.Verified(KeepRules.EnforceKeepOne(chosen, current, rule, home), current, fileId));
            }
            else
            {
                var current = similar;
                var best = bestPhotoIds;
                visible = current.SelectMany(g => g.Images).ToList();
                safe = await Task.Run(() => VerifiedPhotos(chosen, current, best));
            }
            var skipped = chosen.Count - safe.Count;
            var items = visible.Where(f => safe.Contains(f.Path)).Select(Item).ToList();
            if (items.Count == 0)
            {
                Banner = new Banner(BannerKind.Info, "Nada foi removido: os arquivos mudaram desde a busca ou eram a última cópia. Procure de novo.");
                return;
            }
            var result = await services.CleanAsync(items);
            await services.RecordAsync(result, null, "Duplicados");
            var removed = result.Removed.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            groups = groups.Select(g => g with { Files = g.Files.Where(f => !removed.Contains(f.Path)).ToList() }).Where(g => g.Files.Count > 1).ToList();
            similar = similar.Select(g => g with { Images = g.Images.Where(f => !removed.Contains(f.Path)).ToList() }).Where(g => g.Images.Count > 1).ToList();
            ShowIdentical(preserveSimilar: true, applyRule: false);
            ShowSimilar();
            Banner = skipped > 0
                ? new Banner(BannerKind.Info, $"{result.Removed.Count} removido(s). {skipped} arquivo(s) mudaram desde a busca ou eram a última cópia e foram mantidos; procure de novo.",
                    result.Failed.Select(f => $"{f.Item.Name}: {f.Reason}").ToList())
                : Banner.FromClean(result);
        }
        catch (Exception e)
        {
            Log.Write("duplicates remove: " + e);
            Banner = new Banner(BannerKind.Error, "A remoção falhou.", [e.Message]);
        }
        finally { IsRemoving = false; }
    }

    /// <summary>Similar photos: a fully selected group keeps its best photo, and a group whose kept photos all changed or
    /// vanished removes nothing.</summary>
    private HashSet<string> VerifiedPhotos(HashSet<string> chosen, IReadOnlyList<SimilarGroup> current, IReadOnlyDictionary<string, string> best)
    {
        var safe = new HashSet<string>(chosen);
        foreach (var group in current)
        {
            if (group.Images.All(i => safe.Contains(i.Path))) safe.Remove(best.GetValueOrDefault(group.Id) ?? group.Images[0].Path);
            var keeperStillThere = group.Images.Any(i => !safe.Contains(i.Path) && DuplicateVerifier.Unchanged(i, i.Size, fileId));
            foreach (var image in group.Images.Where(i => safe.Contains(i.Path)).ToList())
            {
                if (!keeperStillThere || !DuplicateVerifier.Unchanged(image, image.Size, fileId)) safe.Remove(image.Path);
            }
        }
        safe.IntersectWith(current.SelectMany(g => g.Images.Select(i => i.Path)));
        return safe;
    }
}
