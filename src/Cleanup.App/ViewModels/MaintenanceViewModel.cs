using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.Core.Lists;
using Cleanup.Core.Maintenance;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

/// <summary>One maintenance card.</summary>
public sealed partial class MaintenanceTaskRow : ObservableObject
{
    private readonly Action onSelectionChanged;

    [ObservableProperty] private bool isSelected;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowRunButton))] private bool isRunning;
    [ObservableProperty] private bool controlsEnabled = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasResult), nameof(ShowOk), nameof(ShowFailed))] private bool? succeeded;
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private string resultDetail = "";

    public MaintenanceTaskRow(MaintenanceTask task, Action onSelectionChanged)
    {
        Task = task;
        this.onSelectionChanged = onSelectionChanged;
    }

    partial void OnIsSelectedChanged(bool value) => onSelectionChanged();

    public MaintenanceTask Task { get; }
    public string Id => Task.Id;
    public string Name => Task.Name;
    public string Description => Task.Description;
    public bool IsAdmin => Task.RequiresAdmin;
    public bool IsSlow => Task.IsSlow;
    public bool HasResult => Succeeded is not null;
    public bool ShowOk => Succeeded == true;
    public bool ShowFailed => Succeeded == false;
    public bool ShowRunButton => !IsRunning;

    public string Glyph => Task.Id switch
    {
        MaintenanceTask.FlushDns => "",        // Globe
        MaintenanceTask.FreeRam => "",         // Diagnostic
        MaintenanceTask.ClearThumbnails => "", // Photo
        MaintenanceTask.ResetSearch => "",     // Search
        MaintenanceTask.DismCleanup => "",     // Repair
        MaintenanceTask.Sfc => "",             // Shield
        MaintenanceTask.StorageSense => "",    // HardDrive
        _ => "",
    };
}

/// <summary>
/// Manutenção: task cards with a checkbox. "Executar selecionadas" runs them with at most one UAC prompt; user tasks
/// never run elevated. "Selecionar tudo" leaves out slow tasks.
/// </summary>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    /// <summary>The real runner (UAC helper for admin tasks); swapped only in tests.</summary>
    private readonly Func<IPrivilegedRunner> runner;

    public ObservableCollection<MaintenanceTaskRow> Rows { get; }

    [ObservableProperty] private Banner? banner;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRunSelected))] private bool isRunning;
    [ObservableProperty] private bool isRunningSelection;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRunSelected))] private int selectedCount;
    [ObservableProperty] private bool? selectAllState = false;
    [ObservableProperty] private string selectAllText = "Selecionar tudo";
    [ObservableProperty] private string countLabel = "";
    [ObservableProperty] private bool selectedNeedsAdmin;
    [ObservableProperty] private string runSelectedText = "Executar selecionadas (0)";

    public MaintenanceViewModel() : this(() => MaintenanceRunner.Current) { }

    internal MaintenanceViewModel(Func<IPrivilegedRunner> runner)
    {
        this.runner = runner;
        Rows = new(MaintenanceTask.All.Select(t => new MaintenanceTaskRow(t, UpdateSelection)));
        UpdateSelection();
    }

    public string Subtitle => $"{Rows.Count} tarefas";
    public bool CanRunSelected => !IsRunning && SelectedCount > 0;

    /// <summary>What "Selecionar tudo" covers: everything but the slow or disruptive tasks, which need their own tick.</summary>
    public IReadOnlyList<string> SelectableIds => Rows.Select(r => r.Id).Where(id => !MaintenanceTask.DeliberateOnly.Contains(id)).ToList();

    public void ToggleAll()
    {
        if (IsRunning) return;
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        var target = Selection.Toggled(selected, SelectableIds);
        foreach (var row in Rows) row.IsSelected = target.Contains(row.Id);
    }

    private void UpdateSelection()
    {
        if (Rows is null) return;
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        var state = Selection.State(selected, SelectableIds);
        SelectAllState = state switch { SelectionState.All => true, SelectionState.None => false, _ => null };
        SelectAllText = state == SelectionState.All ? "Desmarcar tudo" : "Selecionar tudo";
        SelectedCount = selected.Count;
        CountLabel = Selection.CountLabel(selected.Count, Rows.Count);
        SelectedNeedsAdmin = Rows.Any(r => r.IsSelected && r.IsAdmin);
        RunSelectedText = $"Executar selecionadas ({selected.Count})";
    }

    public Task RunAsync(MaintenanceTaskRow row) => RunAsync([row], selection: false);

    public Task RunSelectedAsync() => RunAsync(Rows.Where(r => r.IsSelected).ToList(), selection: true);

    /// <summary>Admin tasks share one UAC prompt; the others run as the user (see <see cref="MaintenanceBatch"/>).</summary>
    private async Task RunAsync(IReadOnlyList<MaintenanceTaskRow> chosen, bool selection)
    {
        if (IsRunning || chosen.Count == 0 || SystemViewModels.Optimizer.IsRunning) return;
        IsRunning = true;
        IsRunningSelection = selection;
        Banner = null;
        foreach (var row in Rows) row.ControlsEnabled = false;
        foreach (var row in chosen) row.IsRunning = true;
        try
        {
            var tasks = chosen.Select(r => r.Task).ToList();
            var batchRunner = runner();
            var outcome = await Task.Run(() => MaintenanceBatch.RunAsync(tasks, batchRunner));
            var now = DateTime.Now;
            foreach (var row in chosen)
            {
                if (!outcome.Results.TryGetValue(row.Id, out var result)) continue;
                row.Succeeded = result.Succeeded;
                row.ResultText = result.Succeeded ? $"Concluído às {now:HH:mm}" : "Falhou";
                row.ResultDetail = result.Output;
                if (result.Succeeded) row.IsSelected = false;
            }
            Banner = BannerFor(tasks, outcome);
        }
        catch (Exception e)
        {
            Banner = new Banner(BannerKind.Error, "As tarefas não puderam ser executadas.", [e.Message]);
        }
        finally
        {
            foreach (var row in Rows) { row.IsRunning = false; row.ControlsEnabled = true; }
            IsRunningSelection = false;
            IsRunning = false;
        }
    }

    public static Banner BannerFor(IReadOnlyList<MaintenanceTask> tasks, MaintenanceBatch.Outcome outcome)
    {
        var failed = tasks.Where(t => outcome.Results.TryGetValue(t.Id, out var r) && !r.Succeeded).ToList();
        var cancelled = tasks.Where(t => outcome.CancelledIds.Contains(t.Id)).ToList();
        if (failed.Count == 0 && cancelled.Count == 0)
        {
            var details = tasks.Where(t => outcome.Results[t.Id].Output.Length > 0).Select(t => $"{t.Name}: {outcome.Results[t.Id].Output}").ToList();
            return new Banner(BannerKind.Success, tasks.Count == 1 ? $"{tasks[0].Name}: concluído." : $"{tasks.Count} tarefas concluídas.", details);
        }
        var lines = failed.Select(t => $"{t.Name}: {FailureMessage(outcome.Results[t.Id])}")
            .Concat(cancelled.Select(t => $"{t.Name}: não executada (permissão de administrador não concedida)")).ToList();
        if (failed.Count == 0)
            return new Banner(BannerKind.Info, "Cancelado: a permissão de administrador não foi concedida.", lines);
        return new Banner(BannerKind.Error, $"{failed.Count} de {tasks.Count} tarefa(s) falharam.", lines);
    }

    public static string FailureMessage(CommandResult result) => result.Output.Trim() is { Length: > 0 } output ? output : "sem detalhes";
}
