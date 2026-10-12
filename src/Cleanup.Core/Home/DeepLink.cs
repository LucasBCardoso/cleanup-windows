namespace Cleanup.Core.Home;

/// <summary>
/// <c>cleanup:</c> links (macOS Phase 12): they only navigate or start a read-only scan; nothing that deletes runs
/// from a link (invariant 12). Same rules and shared vectors as the Swift <c>DeepLink</c>.
/// </summary>
public abstract record DeepLink
{
    public sealed record Open(string Module) : DeepLink;
    public sealed record Scan : DeepLink;
    public sealed record History : DeepLink;

    /// <summary>Module identifiers a link may open (the macOS raw values).</summary>
    public static readonly IReadOnlyList<string> Modules =
    [
        "home", "smartScan", "largeFiles", "duplicates", "developer", "privacy", "uninstaller", "updater", "extensions",
        "spaceLens", "optimization", "maintenance",
    ];

    public static DeepLink? Parse(string? link)
    {
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, "cleanup", StringComparison.OrdinalIgnoreCase)) return null;
        var host = uri.Host.ToLowerInvariant();
        switch (host)
        {
            case "hoje" or "home": return new Open("home");
            // Otimização total cleans and asks for permission: the link opens Hoje, where the user presses the button.
            case "optimize" or "otimizar": return new Open("home");
            case "scan" or "verificar": return new Scan();
            case "history" or "historico": return new History();
            case "open":
                // Raw segments: Uri would resolve "../" away, and a traversal must be refused, not normalized.
                var raw = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
                var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
                if (parts.Count != 1) return null;
                var module = Modules.FirstOrDefault(m => string.Equals(m, parts[0], StringComparison.OrdinalIgnoreCase));
                return module is null ? null : new Open(module);
            default: return null;
        }
    }
}
