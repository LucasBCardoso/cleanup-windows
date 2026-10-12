using Cleanup.Core.Home;

namespace Cleanup.Core.Tests.Shared;

public class DeepLinkVectorsTests
{
    private sealed record Vectors(List<string> Modules, List<Vectors.Case> Cases)
    {
        public sealed record Expected(string Action, string? Module);
        public sealed record Case(string Url, Expected? Expected);
    }

    [Fact]
    public void DeepLinks()
    {
        var vectors = SharedFiles.Load<Vectors>("test-vectors/deeplinks.json");
        Assert.Equal(vectors.Modules.Order(), DeepLink.Modules.Order());
        Assert.All(vectors.Cases, c =>
        {
            var actual = DeepLink.Parse(c.Url) switch
            {
                DeepLink.Open o => new Vectors.Expected("open", o.Module),
                DeepLink.Scan => new Vectors.Expected("scan", null),
                DeepLink.History => new Vectors.Expected("history", null),
                _ => null,
            };
            Assert.True(Equals(actual, c.Expected), $"{c.Url}: {actual} vs {c.Expected}");
        });
    }
}
