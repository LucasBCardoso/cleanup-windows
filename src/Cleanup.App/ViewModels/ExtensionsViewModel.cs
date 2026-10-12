using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Extensions;
using Cleanup.Core.Models;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

/// <summary>One installed add-on. Only removable ones get a checkbox.</summary>
public sealed class ExtensionRow(InstalledExtension item) : SelectableRow
{
    public InstalledExtension Item { get; } = item;

    public override string Id => Item.Id;
    public override bool CanSelect => Item.IsRemovable;
    public override long Bytes => Item.Size;
    public override IEnumerable<string> SearchFields =>
        [Item.Name, Item.Path ?? "", Item.Detail ?? "", Item.Browser ?? "", Item.Profile ?? "", .. Item.RegistryKeys];

    public string Name => Item.Name;
    public bool CanReveal => Item.RevealPath is not null;
    public string SizeText => Item.Size > 0 ? ByteFormat.Format(Item.Size) : "";

    public string Subtitle => string.Join(" · ", new[]
    {
        Item.Version is { Length: > 0 } v ? "versão " + v : null,
        Item.Detail,
        Item.Kind is ExtensionKind.ScreenSaver or ExtensionKind.OfficeAddIn && Item.Path is { } p ? Path.GetDirectoryName(p) : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>"Chrome · Default", "Sistema", or nothing for the user's own items (mirrors the Swift placeTag).</summary>
    public string Tag => Item.Browser is { } browser ? $"{browser} · {Item.Profile}" : Item.Scope == ExtensionScope.System ? "Sistema" : "";
    public bool HasTag => Tag.Length > 0;

    public string Glyph => Item.Kind switch
    {
        ExtensionKind.ShellExtension => "", // FileExplorer
        ExtensionKind.Browser => "",        // Puzzle
        ExtensionKind.ScreenSaver => "",    // TVMonitor
        _ => "",                            // Document
    };
}

/// <summary>
/// Extensões: Explorer extensions, browser extensions, screen savers and Office add-ins. Only what the user installed
/// for themselves can be removed (Recycle Bin + history; HKCU keys after a .reg backup); the rest is listed.
/// </summary>
public sealed partial class ExtensionsViewModel : RowList<ExtensionRow>
{
    private readonly AppServices services = AppServices.Shared;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(RemoveCommand))] private bool isLoading;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(RemoveCommand))] private bool isRemoving;
    [ObservableProperty] private bool hasLoaded;
    [ObservableProperty] private Banner? banner;
    [ObservableProperty] private string subtitle = "";

    private static readonly Dictionary<ExtensionKind, string> Notes = new()
    {
        [ExtensionKind.ShellExtension] = "As registradas para todos os usuários aparecem só para consulta. Ao remover uma sua, uma cópia do registro fica em %LOCALAPPDATA%\\Cleanup\\backups.",
        [ExtensionKind.Browser] = "Só as que você instalou pela loja podem ser removidas, com o navegador fechado. Se a sincronização estiver ativa, o navegador pode reinstalá-las: remova-as também por ele.",
        [ExtensionKind.OfficeAddIn] = "Suplementos são gerenciados pelo próprio Office (Arquivo › Opções › Suplementos).",
    };

    protected override void SelectionDidChange() => RemoveCommand.NotifyCanExecuteChanged();

    private bool CanLoad() => !IsLoading && !IsRemoving;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var environment = services.Environment;
            var items = await Task.Run(() =>
            {
                var registry = RegistryExtensions.Current();
                var saver = registry.CurrentScreenSaver();
                var files = new ExtensionInventory(environment) { ExtraScreenSavers = saver is null ? [] : [saver] }.Load();
                return files.Concat(registry.Load()).ToList();
            });
            var selected = AllItems.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            var groups = Enum.GetValues<ExtensionKind>().Select(kind =>
            {
                var rows = items.Where(i => i.Kind == kind).OrderBy(i => i.Scope).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(i => new ExtensionRow(i) { IsSelected = i.IsRemovable && selected.Contains(i.Id) }).ToList();
                Notes.TryGetValue(kind, out var note);
                return (kind.DisplayName(), note, (Func<IReadOnlyList<ExtensionRow>, string>)(r => r.Sum(x => x.Bytes) > 0 ? $"{r.Count} · {SizeOf(r)}" : $"{r.Count}"), (IEnumerable<ExtensionRow>)rows);
            });
            SetRows(groups);
            Subtitle = $"{items.Count} extensões · {ByteFormat.Format(items.Sum(i => i.Size))}";
            HasLoaded = true;
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, "Não foi possível listar as extensões: " + e.Message); }
        finally { IsLoading = false; }
    }

    private bool CanRemove() => !IsLoading && !IsRemoving && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        if (IsRemoving) return;
        var chosen = SelectedRows().Select(r => r.Item).ToList();
        if (chosen.Count == 0) return;
        IsRemoving = true;
        try
        {
            var plan = ExtensionRemoval.Make(chosen, RunningProcesses.Names(), services.Environment);
            var files = await services.CleanAsync(plan.Files);
            await services.RecordAsync(files, null, "Extensões");
            var registry = await ShellExtensionRemover.Current(new ProcessToolRunner()).RemoveAsync(plan.RegistryItems);
            Banner = ResultBanner(files, registry, plan.Refused);
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally { IsRemoving = false; }
        var banner = Banner;
        await LoadAsync();
        Banner = banner;
    }

    internal static Banner ResultBanner(CleanResult files, ShellExtensionRemover.Result registry, IReadOnlyList<(InstalledExtension Item, string Reason)> refused)
    {
        var count = files.Removed.Count + registry.Removed.Count;
        var failures = files.Failed.Select(f => $"{f.Item.Name}: {f.Reason}")
            .Concat(registry.Failed.Select(f => $"{f.Item.Name}: {f.Reason}"))
            .Concat(refused.Select(r => $"{r.Item.Name}: {r.Reason}")).ToList();
        var parts = new List<string>
        {
            count == 0 ? "Nada foi removido." : $"{count} {(count == 1 ? "extensão removida" : "extensões removidas")}"
                + (files.Removed.Count > 0 ? $" ({ByteFormat.Format(files.FreedBytes)} para a Lixeira)." : "."),
        };
        if (registry.Backups.Count > 0) parts.Add("Cópia do registro salva em backups.");
        if (failures.Count > 0) parts.Add($"{failures.Count} não {(failures.Count == 1 ? "pôde" : "puderam")} ser removida(s).");
        return new Banner(failures.Count > 0 ? BannerKind.Warning : BannerKind.Success, string.Join(" ", parts), failures);
    }
}
