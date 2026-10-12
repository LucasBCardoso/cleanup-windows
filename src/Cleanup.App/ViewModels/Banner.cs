using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;
using Cleanup.Platform.Windows;

namespace Cleanup.App.ViewModels;

public enum BannerKind { Success, Info, Warning, Error }

/// <summary>A result line for a module's footer, with optional details (like the macOS FooterBar banner).</summary>
public sealed record Banner(BannerKind Kind, string Message, IReadOnlyList<string>? Details = null)
{
    public string Glyph => Kind switch
    {
        BannerKind.Success => "\uE73E",
        BannerKind.Error => "\uE783",
        BannerKind.Warning => "\uE7BA",
        _ => "\uE946",
    };

    public string DetailsText => Details is { Count: > 0 } d ? string.Join(Environment.NewLine, d.Take(40)) + (d.Count > 40 ? $"{Environment.NewLine}… e mais {d.Count - 40}" : "") : "";
    public bool HasDetails => Details is { Count: > 0 };

    public static Banner FromClean(CleanResult result, AdminCleaner.Result? admin = null)
    {
        var freed = result.FreedBytes + (admin?.Removed.Sum(i => i.Size) ?? 0);
        var count = result.Removed.Count + (admin?.Removed.Count ?? 0);
        var failures = result.Failed.Select(f => $"{f.Item.Name}: {f.Reason}")
            .Concat(admin?.Failed.Select(f => $"{Path.GetFileName(f.Path)}: {f.Reason}") ?? [])
            .ToList();
        var parts = new List<string>();
        parts.Add(count == 0 ? "Nada foi removido." : $"{ByteFormat.Format(freed)} liberados ({count} {(count == 1 ? "item" : "itens")}).");
        if (admin is { Cancelled: true }) parts.Add("Itens do sistema mantidos: a permissão de administrador não foi dada.");
        if (failures.Count > 0) parts.Add($"{failures.Count} não puderam ser removidos.");
        var kind = failures.Count > 0 || admin is { Cancelled: true } ? BannerKind.Warning : BannerKind.Success;
        return new Banner(kind, string.Join(" ", parts), failures);
    }

    public static Banner? Skipped(IEnumerable<string> paths)
    {
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null
            : new Banner(BannerKind.Info, $"{list.Count} {(list.Count == 1 ? "local não pôde" : "locais não puderam")} ser lido(s) — alguns pedem administrador.", list);
    }
}

/// <summary>One line of the Hoje hero: a group and how much "Limpar agora" frees there.</summary>
public sealed record HeroGroup(string Title, string Size);
