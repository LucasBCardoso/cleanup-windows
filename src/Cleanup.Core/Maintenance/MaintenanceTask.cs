using Cleanup.Core.Admin;

namespace Cleanup.Core.Maintenance;

/// <summary>
/// A system maintenance task. Admin tasks run in the elevated helper as one of its fixed operations (never a command
/// line from here); user tasks run in the app as the user, never elevated.
/// </summary>
public sealed record MaintenanceTask(string Id, string Name, string Description, bool RequiresAdmin, bool IsSlow = false)
{
    /// <summary>The step the runner gets: <c>flush-dns</c> is its own helper operation, the others are <c>run &lt;id&gt;</c>.</summary>
    public Elevation.Step Step => Id == FlushDns ? new Elevation.Step(Elevation.Ops.FlushDns) : new Elevation.Step(Elevation.Ops.Run, Id: Id);

    public const string FlushDns = "flush-dns";
    public const string FreeRam = "free-ram";
    public const string ClearThumbnails = "clear-thumbnails";
    public const string ResetSearch = "reset-search";
    public const string DismCleanup = "dism-cleanup";
    public const string Sfc = "sfc";
    public const string StorageSense = "storage-sense";

    /// <summary>Windows counterparts of the macOS list (design §5.2), in the order the cards show.</summary>
    public static readonly IReadOnlyList<MaintenanceTask> All =
    [
        new(FlushDns, "Limpar cache de DNS",
            "Limpa o cache do resolvedor; corrige nomes desatualizados depois de mudanças de rede.", RequiresAdmin: true),
        new(FreeRam, "Liberar RAM",
            "Esvazia a memória de trabalho dos processos e a lista de espera (memória em cache).", RequiresAdmin: true),
        new(ClearThumbnails, "Limpar cache de miniaturas e ícones",
            "Apaga as miniaturas e ícones guardados pelo Explorador de Arquivos; corrige pré-visualizações e ícones antigos ou errados. O Explorador reinicia (a barra de tarefas some por alguns segundos).",
            RequiresAdmin: false),
        new(ResetSearch, "Redefinir o índice de pesquisa",
            "Apaga e reconstrói o índice do Windows Search. A pesquisa fica lenta até terminar.", RequiresAdmin: true, IsSlow: true),
        new(DismCleanup, "Limpar componentes do Windows (DISM)",
            "Remove versões antigas de componentes substituídas por atualizações. Pode levar vários minutos.", RequiresAdmin: true, IsSlow: true),
        new(Sfc, "Verificar arquivos do sistema (sfc)",
            "Procura e repara arquivos do Windows danificados. Pode levar de 10 a 20 minutos.", RequiresAdmin: true, IsSlow: true),
        new(StorageSense, "Sensor de Armazenamento",
            "Abre o Sensor de Armazenamento nas Configurações, para executá-lo agora: ele limpa temporários e a Lixeira conforme as suas regras.",
            RequiresAdmin: false, IsSlow: true),
    ];

    /// <summary>
    /// Slow or disruptive tasks that "Selecionar tudo" leaves out: they need their own tick (macOS: Spotlight reindex,
    /// periodic scripts, Launch Services rebuild).
    /// </summary>
    public static readonly IReadOnlySet<string> DeliberateOnly = All.Where(t => t.IsSlow).Select(t => t.Id).ToHashSet();
}

/// <summary>One step's result: succeeded or not, and what it said.</summary>
public sealed record CommandResult(bool Succeeded, string Output);

/// <summary>
/// What a runner did with a batch: one result per step, or nothing because the UAC prompt was cancelled (nothing ran),
/// or an error that stopped the whole batch (helper missing, the 15-minute timeout…).
/// </summary>
public sealed record PrivilegedRun(bool Cancelled, IReadOnlyList<Elevation.StepResult> Results, string? Error = null)
{
    public static PrivilegedRun WasCancelled { get; } = new(true, []);
    public static PrivilegedRun Failed(string error) => new(false, [], error);
}

/// <summary>
/// Runs a batch of steps: as administrator through the elevated helper (one UAC prompt per batch), or as the user in
/// the app. The Windows counterpart of the Swift <c>PrivilegedRunner</c>.
/// </summary>
public interface IPrivilegedRunner
{
    Task<PrivilegedRun> RunAsync(IReadOnlyList<Elevation.Step> steps, bool asAdmin, CancellationToken cancellation = default);
}

/// <summary>
/// Runs several maintenance tasks with at most one UAC prompt. User tasks run in their own batch as the user — never
/// elevated, where clearing the thumbnail cache or opening Settings would act on the wrong account or desktop.
/// </summary>
public static class MaintenanceBatch
{
    public sealed record Outcome(IReadOnlyDictionary<string, CommandResult> Results, IReadOnlyList<string> CancelledIds);

    public static async Task<Outcome> RunAsync(IReadOnlyList<MaintenanceTask> tasks, IPrivilegedRunner runner, CancellationToken cancellation = default)
    {
        var results = new Dictionary<string, CommandResult>();
        var cancelled = new List<string>();
        foreach (var asAdmin in (bool[])[false, true])
        {
            var group = tasks.Where(t => t.RequiresAdmin == asAdmin).ToList();
            if (group.Count == 0) continue;
            PrivilegedRun run;
            try { run = await runner.RunAsync(group.Select(t => t.Step).ToList(), asAdmin, cancellation); }
            catch (Exception e) when (e is not OperationCanceledException) { run = PrivilegedRun.Failed(e.Message); }
            if (run.Cancelled)
            {
                cancelled.AddRange(group.Select(t => t.Id));
                continue;
            }
            for (var i = 0; i < group.Count; i++)
            {
                results[group[i].Id] = run.Error is { } error ? new CommandResult(false, error)
                    : i < run.Results.Count ? new CommandResult(run.Results[i].Ok, run.Results[i].Message)
                    : new CommandResult(false, "não executada");
            }
        }
        return new Outcome(results, cancelled);
    }
}
