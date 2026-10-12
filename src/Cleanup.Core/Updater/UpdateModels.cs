namespace Cleanup.Core.Updater;

/// <summary>Where an update comes from. winget first, then the Microsoft Store (design §5.2).</summary>
public enum UpdateSourceKind
{
    Winget,
    MicrosoftStore,
}

public static class UpdateSourceKindExtensions
{
    public static string DisplayName(this UpdateSourceKind kind) => kind switch
    {
        UpdateSourceKind.Winget => "winget",
        _ => "Microsoft Store",
    };
}

/// <summary>
/// A newer version of an installed app and how to get it. Mirrors the Swift <c>AvailableUpdate</c>; the app's identity
/// (<see cref="AppId"/>) is its winget package id instead of a bundle ID.
/// </summary>
public sealed record AvailableUpdate
{
    /// <summary>The package id (<c>Microsoft.WindowsTerminal</c>, <c>9NBLGGH4NNS1</c>). Compared ignoring case.</summary>
    public required string AppId { get; init; }
    public required string Name { get; init; }
    public required UpdateSourceKind Source { get; init; }
    public required string CurrentVersion { get; init; }
    public required string NewVersion { get; init; }
    /// <summary>The winget source name as printed (<c>winget</c>, <c>msstore</c>).</summary>
    public string? SourceName { get; init; }
    /// <summary>winget lists it under "require explicit targeting": pinned or flagged by its manifest.</summary>
    public bool RequiresExplicitTargeting { get; init; }
    /// <summary>winget cut the id short ("…"): it can't be installed by id from here.</summary>
    public bool IsIdTruncated { get; init; }
    /// <summary>Tells two installs of the same package apart (x64 and x86, machine and user scope).</summary>
    public int Copy { get; init; }

    /// <summary>Unique per row: two copies of the same package are separate updates.</summary>
    public string Id => Copy == 0 ? $"{Source}:{AppId}".ToLowerInvariant() : $"{Source}:{AppId}#{Copy}".ToLowerInvariant();

    /// <summary>Store updates open the Store; truncated ids can't be targeted. Neither is installed by winget here.</summary>
    public bool CanInstallWithWinget => Source == UpdateSourceKind.Winget && !IsIdTruncated && WingetCommands.IsValidId(AppId);
}

public sealed record UpdateFailure(string AppId, string AppName, string Message);

/// <summary>What one source says about one app. Mirrors the Swift <c>UpdateCheckOutcome</c>.</summary>
public abstract record UpdateCheckOutcome
{
    public sealed record Available(AvailableUpdate Update) : UpdateCheckOutcome;
    public sealed record UpToDate(string AppId) : UpdateCheckOutcome;
    public sealed record Failed(UpdateFailure Failure) : UpdateCheckOutcome;
    /// <summary>The source manages this app but couldn't check it: the app is claimed and the failure reported.</summary>
    public sealed record Unchecked(UpdateFailure Failure) : UpdateCheckOutcome;
}

/// <summary>One place updates can come from. Never throws: failures are outcomes.</summary>
public interface IUpdateSource
{
    UpdateSourceKind Kind { get; }
    Task<IReadOnlyList<UpdateCheckOutcome>> UpdatesAsync(CancellationToken cancellationToken = default);
}

public sealed record UpdateCheckReport(IReadOnlyList<AvailableUpdate> Updates, IReadOnlyList<UpdateFailure> Failures, int IgnoredCount);

/// <summary>
/// Runs every source concurrently and merges the results. Sources are in priority order: the first source with an
/// opinion about an app (available <i>or</i> up to date) owns it. Mirrors the Swift <c>UpdateChecker</c>.
/// </summary>
public sealed class UpdateChecker(IReadOnlyList<IUpdateSource> sources, IgnoredUpdates ignored)
{
    public async Task<UpdateCheckReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var perSource = await Task.WhenAll(sources.Select(s => SafeUpdatesAsync(s, cancellationToken)));
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chosen = new Dictionary<string, AvailableUpdate>();
        var failures = new Dictionary<string, UpdateFailure>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcomes in perSource)
        {
            var claimedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var outcome in outcomes)
            {
                switch (outcome)
                {
                    case UpdateCheckOutcome.Available { Update: var update }:
                        if (claimed.Contains(update.AppId)) continue;
                        chosen[update.Id] = update;
                        claimedNow.Add(update.AppId);
                        break;
                    case UpdateCheckOutcome.UpToDate { AppId: var appId }:
                        if (!claimed.Contains(appId)) claimedNow.Add(appId);
                        break;
                    case UpdateCheckOutcome.Failed { Failure: var failure }:
                        failures.TryAdd(failure.AppId, failure);
                        break;
                    case UpdateCheckOutcome.Unchecked { Failure: var failure }:
                        if (claimed.Contains(failure.AppId)) continue;
                        claimedNow.Add(failure.AppId);
                        failures.TryAdd(failure.AppId, failure);
                        break;
                }
            }
            claimed.UnionWith(claimedNow);
        }
        var visible = chosen.Values.Where(u => !ignored.IsIgnored(u)).ToList();
        var foundIds = chosen.Values.Select(u => u.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new UpdateCheckReport(
            visible.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(u => u.Id, StringComparer.Ordinal).ToList(),
            failures.Values.Where(f => !foundIds.Contains(f.AppId)).OrderBy(f => f.AppName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            chosen.Count - visible.Count);
    }

    /// <summary>A source that throws anyway becomes one failure, so it never hides the others.</summary>
    private static async Task<IReadOnlyList<UpdateCheckOutcome>> SafeUpdatesAsync(IUpdateSource source, CancellationToken cancellationToken)
    {
        try { return await source.UpdatesAsync(cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var name = source.Kind.DisplayName();
            return [new UpdateCheckOutcome.Failed(new UpdateFailure(name, name, e.Message))];
        }
    }
}
