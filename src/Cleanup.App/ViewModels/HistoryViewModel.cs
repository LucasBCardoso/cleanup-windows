using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.App.Services;
using Cleanup.Core.History;
using Cleanup.Core.Models;

namespace Cleanup.App.ViewModels;

public sealed class HistoryRow(CleanupRecord record, Restorer.Availability availability)
{
    public CleanupRecord Record { get; } = record;
    public Restorer.Availability Availability { get; } = availability;
    public string Title => $"{Record.Source} · {ByteFormat.Format(Record.TotalBytes)}";
    public string When => Record.Date.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
    public bool CanUndo => Availability.RestorableCount > 0;

    public string Status
    {
        get
        {
            var parts = new List<string>();
            if (Availability.RestorableCount > 0) parts.Add($"{Availability.RestorableCount} pode(m) voltar ({ByteFormat.Format(Availability.RestorableBytes)})");
            if (Availability.GoneCount > 0) parts.Add($"{Availability.GoneCount} não está(ão) mais na Lixeira");
            if (Availability.BlockedCount > 0) parts.Add($"{Availability.BlockedCount} bloqueado(s)");
            if (Availability.PermanentCount > 0) parts.Add($"{Availability.PermanentCount} apagado(s) de vez");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Past cleanups and what can still come back; Hoje's "Última limpeza" card shows the newest undoable one.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices services = AppServices.Shared;

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty] private HistoryRow? lastUndoable;
    [ObservableProperty] private bool isRestoring;
    [ObservableProperty] private Banner? banner;

    public HistoryViewModel()
    {
        services.HistoryChanged += () => App.Window?.DispatcherQueue.TryEnqueue(() => _ = ReloadAsync());
    }

    public async Task ReloadAsync()
    {
        try
        {
            var context = services.RestoreContext();
            var rows = await Task.Run(() => CleanupHistory.Load().Records.Select(r => new HistoryRow(r, Restorer.AvailabilityOf(r, context))).ToList());
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            LastUndoable = rows.FirstOrDefault(r => r.CanUndo);
        }
        catch (CleanupHistoryException e) { Banner = new Banner(BannerKind.Error, e.Message); }
    }

    public async Task UndoAsync(HistoryRow row)
    {
        if (IsRestoring) return;
        IsRestoring = true;
        try
        {
            var context = services.RestoreContext();
            var result = await Task.Run(() => Restorer.Restore(row.Record, context));
            await Task.Run(() => CleanupHistory.Update(h => h.ApplyingRestore(row.Record.Id, result.Restored)));
            var failures = result.Failed.Select(f => $"{Path.GetFileName(f.Entry.OriginalPath)}: {f.Reason}").ToList();
            Banner = result.Restored.Count == 0
                ? new Banner(BannerKind.Warning, "Nada pôde voltar.", failures)
                : new Banner(failures.Count > 0 ? BannerKind.Warning : BannerKind.Success,
                    $"{result.Restored.Count} {(result.Restored.Count == 1 ? "item voltou" : "itens voltaram")} para o lugar ({ByteFormat.Format(result.RestoredBytes)})."
                    + (failures.Count > 0 ? $" {failures.Count} não." : ""), failures);
        }
        catch (Exception e) { Banner = new Banner(BannerKind.Error, e.Message); }
        finally
        {
            IsRestoring = false;
            services.NotifyHistoryChanged();
        }
    }
}
