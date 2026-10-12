using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Cleanup.Core.Admin;
using Cleanup.Core.Models;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Runs a batch of steps in <c>Cleanup.Elevated.exe</c>: one UAC prompt per user action (invariant 5). The app opens a
/// one-instance named pipe with a random name, starts the helper with <c>runas</c>, sends the request and reads one
/// result per step. A cancelled prompt runs nothing and is reported as such.
/// </summary>
public static class ElevatedRunner
{
    public sealed record Outcome(bool Cancelled, IReadOnlyList<Elevation.StepResult> Results, string? Error = null);

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Cleanup.Elevated.exe");

    public static async Task<Outcome> RunAsync(IReadOnlyList<Elevation.Step> steps, CancellationToken cancellation = default)
    {
        if (steps.Count == 0) return new Outcome(false, []);
        if (!File.Exists(HelperPath)) return new Outcome(false, [], $"auxiliar não encontrado: {HelperPath}");
        var name = "cleanup-elevated-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        Process? helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo(HelperPath, ["--pipe", name, "--parent", Environment.ProcessId.ToString()])
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: the user said no
        {
            return new Outcome(true, []);
        }
        if (helper is null) return new Outcome(false, [], "o auxiliar não iniciou");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Timeout);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Elevation.Request(steps), Elevation.Json));
            var line = await reader.ReadLineAsync(timeout.Token);
            var response = line is null ? null : JsonSerializer.Deserialize<Elevation.Response>(line, Elevation.Json);
            if (response is null || response.Results.Count != steps.Count)
                return new Outcome(false, [], "o auxiliar não respondeu");
            return new Outcome(false, response.Results);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new Outcome(false, [], "o Cleanup parou de esperar depois de 15 minutos (a tarefa pode continuar rodando)");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new Outcome(false, [], e.Message);
        }
        finally
        {
            helper.Dispose();
        }
    }
}

/// <summary>System junk: planned against <see cref="AdminPathPolicy"/>, removed permanently by the elevated helper.</summary>
public static class AdminCleaner
{
    public sealed record CleanPlan(IReadOnlyList<ScanItem> Accepted, IReadOnlyList<(ScanItem Item, string Reason)> Rejected);

    public sealed record Failure(string Path, string Reason);

    public sealed record Result(bool Cancelled, IReadOnlyList<ScanItem> Removed, IReadOnlyList<Failure> Failed);

    public static CleanPlan Plan(IEnumerable<ScanItem> items, ScanEnvironment environment)
    {
        var policy = AdminPathPolicy.For(environment);
        var accepted = new List<ScanItem>();
        var rejected = new List<(ScanItem, string)>();
        foreach (var item in items)
        {
            if (policy.Reject(item.Path) is { } reason) rejected.Add((item, reason));
            else accepted.Add(item);
        }
        return new CleanPlan(accepted, rejected);
    }

    public static async Task<Result> RunAsync(IReadOnlyList<ScanItem> items, IReadOnlyList<Elevation.Step>? extraSteps = null,
        CancellationToken cancellation = default)
    {
        var steps = items.Select(i => new Elevation.Step(Elevation.Ops.Remove, i.Path)).Concat(extraSteps ?? []).ToList();
        var outcome = await ElevatedRunner.RunAsync(steps, cancellation);
        if (outcome.Cancelled) return new Result(true, [], []);
        if (outcome.Error is { } error) return new Result(false, [], items.Select(i => new Failure(i.Path, error)).ToList());
        var removed = new List<ScanItem>();
        var failed = new List<Failure>();
        for (var i = 0; i < items.Count; i++)
        {
            if (outcome.Results[i].Ok) removed.Add(items[i]);
            else failed.Add(new Failure(items[i].Path, outcome.Results[i].Message));
        }
        return new Result(false, removed, failed);
    }
}
