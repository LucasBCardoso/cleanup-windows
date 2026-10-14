using System.Security.Cryptography;
using Cleanup.Core.SelfUpdate;
using Cleanup.Core.Tests.Shared;

namespace Cleanup.Core.Tests.Updater;

public sealed class ReleaseFeedTests
{
    private const string Sha = "715234dd22a760ed408f2b0df9dac8d1c3f8097683baf8841f8528e97e31d703";

    private static string Json(string tag = "v1.3.12", string? name = null, string url = "https://github.com/LucasBCardoso/cleanup-windows/releases/download/v1.3.12/Cleanup-Setup-1.3.12.exe",
        string digest = "sha256:" + Sha, bool draft = false, bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/LucasBCardoso/cleanup-windows/releases/tag/{{tag}}",
          "assets": [
            { "name": "Cleanup-Setup-1.3.12.exe.sha256", "browser_download_url": "https://github.com/x/y.sha256", "digest": "sha256:{{Sha}}" },
            { "name": "{{name ?? "Cleanup-Setup-1.3.12.exe"}}", "browser_download_url": "{{url}}", "digest": "{{digest}}" }
          ]
        }
        """;

    [Fact]
    public void ParsesTheLatestRelease()
    {
        var release = ReleaseFeed.Parse(Json())!;
        Assert.Equal(new Version(1, 3, 12), release.Version);
        Assert.Equal("Cleanup-Setup-1.3.12.exe", release.InstallerName);
        Assert.Equal(Sha, release.Sha256);
        Assert.EndsWith("/Cleanup-Setup-1.3.12.exe", release.Installer.AbsoluteUri);
        Assert.EndsWith("/tag/v1.3.12", release.Page.AbsoluteUri);
    }

    /// <summary>GitHub's real answer for the v1.2.0 release, as recorded on 2026-10-14.</summary>
    [Fact]
    public void ParsesARecordedGitHubAnswer()
    {
        var json = File.ReadAllText(Path.Combine(SharedFiles.RepoRoot, "tests", "Cleanup.Core.Tests", "Fixtures", "github", "latest-release-v1.2.0.json"));
        var release = ReleaseFeed.Parse(json)!;
        Assert.Equal(new Version(1, 2, 0), release.Version);
        Assert.Equal("https://github.com/LucasBCardoso/cleanup-windows/releases/download/v1.2.0/Cleanup-Setup-1.2.0.exe", release.Installer.AbsoluteUri);
        Assert.Equal("715234dd22a760ed408f2b0df9dac8d1c3f8097683baf8841f8528e97e31d703", release.Sha256);
        Assert.Equal("https://github.com/LucasBCardoso/cleanup-windows/releases/tag/v1.2.0", release.Page.AbsoluteUri);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("bad tag")]
    [InlineData("other installer")]
    [InlineData("http")]
    [InlineData("other host")]
    [InlineData("no digest")]
    [InlineData("md5 digest")]
    public void RefusesWhatItCannotTrust(string why)
    {
        var json = why switch
        {
            "draft" => Json(draft: true),
            "prerelease" => Json(prerelease: true),
            "bad tag" => Json(tag: "v1.3.12-beta"),
            "other installer" => Json(name: "Cleanup-Setup-1.3.11.exe"),
            "http" => Json(url: "http://github.com/LucasBCardoso/cleanup-windows/releases/download/v1.3.12/Cleanup-Setup-1.3.12.exe"),
            "other host" => Json(url: "https://evil.example/Cleanup-Setup-1.3.12.exe"),
            "no digest" => Json(digest: ""),
            _ => Json(digest: "md5:0123456789abcdef0123456789abcdef"),
        };
        Assert.Null(ReleaseFeed.Parse(json));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    [InlineData("""{"tag_name":"v1.3.12","assets":{}}""")]
    public void RefusesBrokenAnswers(string json) => Assert.Null(ReleaseFeed.Parse(json));

    [Theory]
    [InlineData("v1.3.12", "1.3.12")]
    [InlineData("v10.0.0", "10.0.0")]
    [InlineData("1.3.12", null)]
    [InlineData("v1.3", null)]
    [InlineData("v1.3.12.0", null)]
    [InlineData("v1\";calc;\"", null)]
    [InlineData(null, null)]
    public void TagVersion(string? tag, string? expected) => Assert.Equal(expected, ReleaseFeed.TagVersion(tag)?.ToString());

    [Theory]
    [InlineData("1.3.12", "1.3.11.0", true)]
    [InlineData("1.3.12", "1.2.0.0", true)]
    [InlineData("1.3.12", "1.3.12.0", false)]
    [InlineData("1.3.12", "1.3.12.7", false)]
    [InlineData("1.3.12", "1.4.0.0", false)]
    [InlineData("1.3.0", "1.3", false)]
    public void IsNewer(string release, string current, bool newer) =>
        Assert.Equal(newer, ReleaseFeed.IsNewer(new AppRelease(Version.Parse(release), new Uri("https://github.com/x"), Sha, ReleaseFeed.ReleasesPage), Version.Parse(current)));

    [Fact]
    public void MatchesOnlyTheExactBytes()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var sha = Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant();
            Assert.True(ReleaseFeed.Matches(path, sha));
            File.WriteAllBytes(path, [1, 2, 4]);
            Assert.False(ReleaseFeed.Matches(path, sha));
        }
        finally { File.Delete(path); }
    }
}
