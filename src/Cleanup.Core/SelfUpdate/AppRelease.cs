using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cleanup.Core.SelfUpdate;

/// <summary>A published Cleanup release: its version, the Setup.exe to download and the SHA-256 GitHub computed for it.</summary>
public sealed record AppRelease(Version Version, Uri Installer, string Sha256, Uri Page)
{
    public string InstallerName => $"Cleanup-Setup-{Version.ToString(3)}.exe";
}

/// <summary>
/// The app updating itself from GitHub Releases (every push to main publishes vX.Y.N with Cleanup-Setup-X.Y.N.exe).
/// Only a release with a well-formed tag, its matching installer on github.com over https and a SHA-256 digest is
/// trusted; the download is installed only when its hash matches and, once Cleanup itself is code-signed, when the
/// installer is signed by the same publisher (<see cref="SignatureAccepted"/>).
/// </summary>
public static partial class ReleaseFeed
{
    public const string Repository = "LucasBCardoso/cleanup-windows";
    public static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");

    /// <summary>GitHub's "latest release" JSON → the release, or null when it can't be trusted or used.</summary>
    public static AppRelease? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;
            if (TagVersion(Text(root, "tag_name")) is not { } version) return null;
            var expected = $"Cleanup-Setup-{version.ToString(3)}.exe";
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(Text(asset, "name"), expected, StringComparison.Ordinal)) continue;
                if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url) || !IsGitHub(url)) return null;
                if (Sha256Of(Text(asset, "digest")) is not { } sha) return null;
                var page = Uri.TryCreate(Text(root, "html_url"), UriKind.Absolute, out var html) && IsGitHub(html) ? html : ReleasesPage;
                return new AppRelease(version, url, sha, page);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>"v1.3.12" → 1.3.12; anything else (no v, two or four parts, suffixes) → null.</summary>
    public static Version? TagVersion(string? tag) =>
        tag is not null && TagPattern().Match(tag) is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;

    /// <summary>Whether <paramref name="release"/> is newer than the running build (compared on major.minor.build).</summary>
    public static bool IsNewer(AppRelease release, Version? current) =>
        current is null || release.Version > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>The file's SHA-256 equals the expected lowercase hex digest.</summary>
    public static bool Matches(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(sha256));
    }

    /// <summary>
    /// The Authenticode rule on top of the SHA-256: a signed Cleanup (<paramref name="running"/> = its signer) installs
    /// only an installer validly signed by that same signer; an unsigned one (≤1.3.x, development builds) accepts any,
    /// so it can move to the first signed release. Signer names are compared ordinally; null means unsigned or invalid.
    /// </summary>
    public static bool SignatureAccepted(string? running, string? download) =>
        running is null || string.Equals(running, download, StringComparison.Ordinal);

    private static string? Sha256Of(string? digest) =>
        digest is not null && DigestPattern().Match(digest) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    private static bool IsGitHub(Uri url) => url.Scheme == Uri.UriSchemeHttps && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsTrue(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"^v(\d{1,5})\.(\d{1,5})\.(\d{1,5})$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex DigestPattern();
}
