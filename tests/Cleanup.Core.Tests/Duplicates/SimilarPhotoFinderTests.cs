using Cleanup.Core.Duplicates;

namespace Cleanup.Core.Tests.Duplicates;

/// <summary>Prints by file name: "<hash>-<width>x<height>" written into the file's content.</summary>
public sealed class FakeFingerprinter : IImageFingerprinter
{
    public Dictionary<string, ImagePrint?> Prints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Calls { get; private set; }

    public ImagePrint? Fingerprint(string path)
    {
        Calls++;
        return Prints.TryGetValue(Path.GetFileName(path), out var print) ? print : new ImagePrint(0, 10, 10);
    }
}

public class SimilarPhotoFinderTests
{
    private static WalkedFile File(string name, long size = 100) => new($@"C:\p\{name}", size, null, null, "id:" + name);

    /// <summary>A smooth synthetic "photo" (gradients and a blob), optionally brightened, as 32×32 luminance.</summary>
    private static byte[] Picture(int seed, double brightness = 1, int shift = 0)
    {
        var random = new Random(seed);
        double cx = random.Next(8, 24), cy = random.Next(8, 24), angle = random.NextDouble() * Math.PI;
        var pixels = new byte[32 * 32];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var sx = x + shift;
                var gradient = 100 + 60 * Math.Cos(angle) * (sx - 16) / 16 + 60 * Math.Sin(angle) * (y - 16) / 16;
                var blob = 80 * Math.Exp(-((sx - cx) * (sx - cx) + (y - cy) * (y - cy)) / 40);
                pixels[y * 32 + x] = (byte)Math.Clamp((gradient + blob) * brightness, 0, 255);
            }
        }
        return pixels;
    }

    [Fact]
    public void ResizedCopyMatchesAndADifferentPictureDoesNot()
    {
        var original = PerceptualHash.Compute(Picture(1));
        var brighter = PerceptualHash.Compute(Picture(1, brightness: 1.1));
        var shifted = PerceptualHash.Compute(Picture(1, shift: 1));
        var other = PerceptualHash.Compute(Picture(2));
        Assert.True(PerceptualHash.Distance(original, brighter) <= SimilarityLevel.Strict.MaxDistance());
        Assert.True(PerceptualHash.Distance(original, shifted) <= SimilarityLevel.Normal.MaxDistance());
        Assert.True(PerceptualHash.Distance(original, other) > SimilarityLevel.Loose.MaxDistance());
        Assert.True(PerceptualHash.IsFlat(new byte[32 * 32]));
        Assert.False(PerceptualHash.IsFlat(Picture(1)));
    }

    [Fact]
    public void LevelsMapToThresholds()
    {
        Assert.Equal([4, 8, 12], SimilarityLevels.All.Select(l => l.MaxDistance()));
        Assert.Equal(["Quase idênticas", "Parecidas", "Bem parecidas (inclui sequências)"], SimilarityLevels.All.Select(l => l.Title()));
    }

    [Fact]
    public void ExactDuplicatesAreLeftToTheOtherTab()
    {
        var fake = new FakeFingerprinter();
        var a = File("a.jpg");
        var b = File("b.jpg");
        var result = SimilarPhotoFinder.Groups([a, b], new HashSet<string> { b.Path }, 8, fake, CancellationToken.None);
        Assert.Empty(result.Groups);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void UnionFindGroupsTransitively()
    {
        var groups = SimilarPhotoFinder.Cluster(5, [(0, 1), (1, 2), (3, 3)]);
        var group = Assert.Single(groups);
        Assert.Equal([0, 1, 2], group.Order());
    }

    [Fact]
    public void GroupsUnreadableFlatAndBestImage()
    {
        var fake = new FakeFingerprinter();
        fake.Prints["a.jpg"] = new ImagePrint(0b1111, 100, 100);
        fake.Prints["b.jpg"] = new ImagePrint(0b0111, 400, 300);   // 1 bit away, more pixels: the best
        fake.Prints["c.jpg"] = new ImagePrint(ulong.MaxValue, 10, 10);
        fake.Prints["d.jpg"] = null;                               // can't be decoded
        fake.Prints["e.jpg"] = new ImagePrint(0b1111, 10, 10, Flat: true);
        var result = SimilarPhotoFinder.Groups([File("a.jpg"), File("b.jpg"), File("c.jpg"), File("d.jpg"), File("e.jpg")],
            new HashSet<string>(), 4, fake, CancellationToken.None);
        var group = Assert.Single(result.Groups);
        Assert.Equal(["a.jpg", "b.jpg"], group.Images.Select(i => i.Name));
        Assert.Equal(1, result.Skipped);
        Assert.False(result.Partial);
        Assert.Equal(@"C:\p\b.jpg", result.BestImageIds[group.Id]);
    }

    [Fact]
    public void CancellationReturnsPartial()
    {
        var result = SimilarPhotoFinder.Groups([File("a.jpg"), File("b.jpg")], new HashSet<string>(), 8, new FakeFingerprinter(),
            new CancellationToken(canceled: true));
        Assert.True(result.Partial);
        Assert.Empty(result.Groups);
    }
}
