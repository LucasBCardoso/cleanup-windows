using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cleanup.App.Services;
using Cleanup.Core.Models;
using Cleanup.Core.Privacy;
using Cleanup.Core.Rules;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

/// <summary>One Privacidade row: a privacy item (files of one app and kind) or a saved Wi-Fi network.</summary>
public sealed class PrivacyRow : ItemRow
{
    public PrivacyItem? Privacy { get; }
    public string? Ssid { get; }

    public PrivacyRow(PrivacyItem item, bool selected, Action onSelectionChanged)
        : base(new ScanItem
        {
            // The first file stands for the item: unique per item, and the search box matches its folder.
            Path = item.Paths[0], Size = item.Size, RuleId = "privacy", Category = ScanCategory.SystemJunk, Risk = RiskLevel.Review,
            DisplayName = TitleOf(item),
        }, selected, onSelectionChanged) => Privacy = item;

    public PrivacyRow(string ssid, bool selected, Action onSelectionChanged)
        : base(new ScanItem
        {
            // SSIDs are case-sensitive while row ids aren't: the hex of the name keeps two networks apart.
            Path = WiFiKey(ssid), Size = 0, RuleId = "privacy-wifi", Category = ScanCategory.SystemJunk, Risk = RiskLevel.Review,
            DisplayName = ssid, Kind = ItemKind.Path,
        }, selected, onSelectionChanged) => Ssid = ssid;

    public static string WiFiKey(string ssid) => @"wifi:\" + Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));

    public static string TitleOf(PrivacyItem item) =>
        item.Kind.Title() + (item.Profile is { } p && p != "Default" ? " · " + p : "");

    public override bool IsEnabled => Privacy is null || !Privacy.IsBlocked;
    public bool IsWiFi => Ssid is not null;
    public bool IsBlocked => Privacy?.IsBlocked == true;
    public string BlockedReason => Privacy?.BlockedReason ?? "";
    public string Title => Ssid ?? TitleOf(Privacy!);
    public string Subtitle => Ssid is not null ? "Esquecer esta rede" : Privacy!.Loses;
    public string Trailing => Ssid is not null ? "" : SizeText;
    public double RowOpacity => IsBlocked ? 0.6 : 1;

    public string Glyph => Privacy?.Kind switch
    {
        null => "",                       // Wifi
        PrivacyKind.History => "",        // History
        PrivacyKind.Cookies => "",        // Globe
        PrivacyKind.Forms => "",          // Edit
        PrivacyKind.RecentItems => "",    // Recent
        _ => "",                          // CommandPrompt
    };

    /// <summary>The line the confirmation shows for this row: what the user loses.</summary>
    public string ConfirmationLine => Ssid is not null ? Ssid : $"{Privacy!.App} — {TitleOf(Privacy)}: {Privacy.Loses}";
}

/// <summary>
/// Privacidade: browsing traces, recent items, PowerShell history and saved Wi-Fi networks. Nothing is pre-selected;
/// deletion is permanent and the view confirms it first, listing what is lost (invariant 11).
/// </summary>
public sealed partial class PrivacyViewModel : SelectableList
{
    public const string WiFiGroup = "Redes Wi-Fi salvas";

    private readonly AppServices services = AppServices.Shared;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoadCommand))] [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowEmpty), nameof(IsIdle), nameof(CanClean))]
    private bool isLoading;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoadCommand))] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanClean))]
    private bool isCleaning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowEmpty))] private bool hasLoaded;
    [ObservableProperty] private Banner? banner;
    [ObservableProperty] private string wiFiNote = "";

    private WiFiState? wifi;

    public bool ShowLoading => IsLoading && !HasLoaded;
    public bool ShowEmpty => HasLoaded && !IsLoading && IsEmpty;
    /// <summary>Rows and select-all are disabled while cleaning, so the batch is exactly what the confirmation listed.</summary>
    public bool IsIdle => !IsLoading && !IsCleaning;

    public bool HasSelection => SelectedCount > 0;

    private bool CanLoad() => !IsLoading && !IsCleaning;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var environment = services.Environment;
            var (result, state) = await Task.Run(() =>
                (new PrivacyScanner(environment, RunningProcesses.Names()).Scan(), WiFi.Load()));
            var selected = AllRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            wifi = state;
            var groups = result.Items.GroupBy(i => i.App)
                .Select(g => (g.Key, (IEnumerable<ItemRow>)g.Select(i =>
                    new PrivacyRow(i, !i.IsBlocked && selected.Contains(i.Paths[0].ToLowerInvariant()), UpdateSelection)).ToList()))
                .ToList();
            if (state is { Networks.Count: > 0 })
            {
                groups.Add((WiFiGroup, state.Networks.Select(n =>
                    new PrivacyRow(n, selected.Contains(PrivacyRow.WiFiKey(n).ToLowerInvariant()), UpdateSelection)).ToList()));
            }
            SetRows(groups);
            WiFiNote = state is null ? "" : state.Connected.Count > 0
                ? $"A rede em uso ({string.Join(", ", state.Connected.Order(StringComparer.Ordinal))}) não aparece aqui e nunca é removida."
                : "A rede em uso nunca é removida: na hora de remover, o Cleanup confere qual é e a mantém.";
            HasLoaded = true;
            Banner ??= Banner.Skipped(result.Unreadable);
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    protected override string? GroupTrailing(string title, IReadOnlyList<ItemRow> rows) =>
        title == WiFiGroup ? $"{rows.Count} {(rows.Count == 1 ? "rede" : "redes")}" : null;

    protected override void UpdateSelection()
    {
        base.UpdateSelection();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanClean));
    }

    /// <summary>What the confirmation lists and the clean then erases: captured once, before any await.</summary>
    public sealed record Plan(IReadOnlyList<PrivacyItem> Items, IReadOnlyList<string> Networks, IReadOnlySet<string> Connected)
    {
        public bool IsEmpty => Items.Count == 0 && Networks.Count == 0;

        public string ConfirmationText
        {
            get
            {
                var lines = Items.Select(i => $"• {i.App} — {PrivacyRow.TitleOf(i)}: {i.Loses}").ToList();
                if (Networks.Count > 0)
                    lines.Add($"• Redes Wi-Fi esquecidas: {string.Join(", ", Networks)} (pede permissão de administrador).");
                return string.Join("\n", lines) + "\n\nIsso não vai para a Lixeira e não dá para desfazer. Senhas, favoritos e configurações ficam.";
            }
        }
    }

    public Plan CurrentPlan()
    {
        var rows = AllRows.OfType<PrivacyRow>().Where(r => r.IsSelected && r.IsEnabled).ToList();
        return new Plan(rows.Where(r => r.Privacy is not null).Select(r => r.Privacy!).ToList(),
            rows.Where(r => r.Ssid is not null).Select(r => r.Ssid!).ToList(),
            wifi?.Connected ?? new HashSet<string>());
    }

    /// <summary>The footer button: the view confirms first, then calls <see cref="CleanAsync(Plan)"/> with the plan it showed.</summary>
    public bool CanClean => !IsLoading && !IsCleaning && HasSelection;

    /// <summary>Erases the plan's files permanently, then forgets its Wi-Fi networks in one elevated session.</summary>
    public async Task CleanAsync(Plan plan)
    {
        if (IsCleaning || IsLoading || plan.IsEmpty) return;
        IsCleaning = true;
        try
        {
            var policy = services.Policy();
            var remover = services.Remover;
            // Running apps are checked again now: a browser opened since the scan would write its data back.
            var result = await Task.Run(() => PrivacyCleaner.Clean(plan.Items, RunningProcesses.Names(), policy, remover));
            var wifiOutcome = plan.Networks.Count > 0
                ? await WiFi.ForgetAsync(plan.Networks, plan.Connected)
                : new WiFi.Outcome([], [], [], false);
            Banner = BannerFor(result, wifiOutcome);
            foreach (var row in AllRows) row.IsSelected = false;
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally { IsCleaning = false; }
        await LoadAsync();
    }

    internal static Banner BannerFor(PrivacyCleanResult result, WiFi.Outcome wifi)
    {
        var parts = new List<string>();
        if (result.Cleaned.Count > 0)
            parts.Add($"{result.Cleaned.Count} {(result.Cleaned.Count == 1 ? "item apagado" : "itens apagados")} ({ByteFormat.Format(result.FreedBytes)})");
        if (wifi.Removed.Count > 0)
            parts.Add($"{wifi.Removed.Count} {(wifi.Removed.Count == 1 ? "rede Wi-Fi esquecida" : "redes Wi-Fi esquecidas")}");
        var problems = result.Refused.Select(r => $"{r.Item.App}: {r.Reason}").ToList();
        // One line per file that stayed; the items they belong to were only partly erased.
        problems.AddRange(result.Failed.Select(f => $"{Path.GetFileName(f.Path)}: {f.Reason}"));
        problems.AddRange(wifi.Failed);
        var notes = wifi.Kept.Select(n => $"{n}: mantida (rede em uso ou nome não suportado)").ToList();
        var summary = parts.Count == 0 ? "Nada foi apagado." : string.Join(" e ", parts) + ".";
        if (result.Failed.Count > 0) summary += " Alguns arquivos não puderam ser apagados (itens parcialmente apagados).";
        if (wifi.Cancelled)
            return new Banner(BannerKind.Info, summary + " Sem a permissão de administrador, as redes Wi-Fi ficaram.", [.. problems, .. notes]);
        if (problems.Count > 0)
        {
            var count = result.Refused.Count + wifi.Failed.Count + (result.Failed.Count > 0 ? 1 : 0);
            return new Banner(BannerKind.Warning, summary + $" {count} {(count == 1 ? "problema" : "problemas")}.", [.. problems, .. notes]);
        }
        return notes.Count == 0 ? new Banner(BannerKind.Success, summary) : new Banner(BannerKind.Info, summary, notes);
    }
}

/// <summary>The Privacidade view model outlives its view (kept here so the module stays self-contained).</summary>
public static class PrivacyViewModels
{
    public static PrivacyViewModel Shared { get; } = new();
}
