namespace Cleanup.Core.Safety;

/// <summary>
/// Re-checks every item at clean time, whichever scanner produced it (invariant 2). The full policy arrives in W1;
/// W0 carries the component-aware containment rule shared with macOS (<c>shared/test-vectors/path-containment.json</c>).
/// </summary>
public sealed partial class SafetyPolicy
{
    /// <summary>
    /// True when <paramref name="path"/> equals <paramref name="root"/> or lies underneath it, by path component,
    /// never by raw prefix. <c>\</c> is treated as <c>/</c> and the comparison ignores case, as NTFS does.
    /// </summary>
    public static bool IsWithin(string path, string root)
    {
        var p = path.Replace('\\', '/');
        var r = root.Replace('\\', '/');
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r.EndsWith('/') ? r : r + "/";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
