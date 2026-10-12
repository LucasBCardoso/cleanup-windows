using Cleanup.Core.Duplicates;
using Cleanup.Platform.Windows;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit.Abstractions;

namespace Cleanup.Platform.Tests;

/// <summary>
/// Calibrates the similar-photo levels on the pictures every Windows install ships (<c>C:\Windows\Web</c>), like the
/// macOS threshold was calibrated on the macOS wallpapers: resized + recompressed copies and light edits must match at
/// the matching level, different pictures must not match even at the loosest level.
/// </summary>
public class PerceptualHashCalibrationTests(ITestOutputHelper output)
{
    private static readonly WindowsImageFingerprinter Fingerprinter = new();

    internal static List<string> Wallpapers() =>
        Directory.Exists(@"C:\Windows\Web")
            ? Directory.EnumerateFiles(@"C:\Windows\Web", "*.jpg", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>"img0.jpg" and "img0_1920x1200.jpg" are the same picture at two sizes.</summary>
    private static string Picture(string path) => Path.GetFileNameWithoutExtension(path).Split('_')[0].ToLowerInvariant();

    private static string Folder(string path) => Path.GetDirectoryName(path)!.ToLowerInvariant();

    private static bool Reuses(string path) => Folder(path).EndsWith(@"\spotlight") || Folder(path).EndsWith(@"\screen");

    /// <summary>Re-encodes <paramref name="source"/> as a JPEG: scaled, optionally cropped and brightened.</summary>
    internal static async Task Reencode(string source, string destination, double scale, double quality, double crop = 0, double brighten = 1)
    {
        byte[] pixels;
        uint width, height;
        await using (var input = File.OpenRead(source))
        {
            var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
            width = (uint)(decoder.OrientedPixelWidth * scale);
            height = (uint)(decoder.OrientedPixelHeight * scale);
            var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
            if (crop > 0)
            {
                var dx = (uint)(width * crop);
                var dy = (uint)(height * crop);
                transform.Bounds = new BitmapBounds { X = dx, Y = dy, Width = width - 2 * dx, Height = height - 2 * dy };
                width -= 2 * dx;
                height -= 2 * dy;
            }
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            pixels = data.DetachPixelData();
        }
        if (brighten != 1)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                if (i % 4 != 3) pixels[i] = (byte)Math.Min(255, pixels[i] * brighten);
            }
        }
        await using var output = File.Create(destination);
        using var stream = output.AsRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, global::Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    [Fact]
    public async Task WallpapersCalibrateTheLevels()
    {
        var wallpapers = Wallpapers();
        if (wallpapers.Count < 4) return; // not a stock Windows install
        var dir = Directory.CreateTempSubdirectory("cleanup-phash-").FullName;
        try
        {
            var prints = wallpapers.ToDictionary(w => w, w => Fingerprinter.Fingerprint(w)!);
            // Solid-color pictures are never compared (their hash is noise).
            Assert.Contains(prints.Values, p => p.Flat);
            wallpapers = wallpapers.Where(w => !prints[w].Flat).ToList();
            var contents = wallpapers.ToDictionary(w => w, w => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(w))));
            var resized = new List<int>();
            var edited = new List<int>();
            foreach (var (wallpaper, i) in wallpapers.Select((w, i) => (w, i)))
            {
                var copy = Path.Combine(dir, $"{i}-small.jpg");
                await Reencode(wallpaper, copy, 0.5, 0.6);
                resized.Add(PerceptualHash.Distance(prints[wallpaper].Hash, Fingerprinter.Fingerprint(copy)!.Hash));
                var edit = Path.Combine(dir, $"{i}-edit.jpg");
                await Reencode(wallpaper, edit, 0.75, 0.85, crop: 0.04, brighten: 1.12);
                edited.Add(PerceptualHash.Distance(prints[wallpaper].Hash, Fingerprinter.Fingerprint(edit)!.Hash));
            }
            var same = new List<int>();
            var different = new List<int>();
            for (var i = 0; i < wallpapers.Count; i++)
            {
                for (var j = i + 1; j < wallpapers.Count; j++)
                {
                    string a = wallpapers[i], b = wallpapers[j];
                    var d = PerceptualHash.Distance(prints[a].Hash, prints[b].Hash);
                    if (Picture(a) == Picture(b)) same.Add(d);
                    // Different pictures: other folders (a theme folder is one composition in several colors, and
                    // Spotlight and the lock screen reuse theme pictures), never byte copies (Windows hard-links some of them).
                    else if (Folder(a) != Folder(b) && contents[a] != contents[b] && !Reuses(a) && !Reuses(b))
                        different.Add(d);
                }
            }
            string Describe(List<int> d) => d.Count == 0 ? "—" : $"min {d.Min()} · median {d.Order().ElementAt(d.Count / 2)} · max {d.Max()} (n={d.Count})";
            output.WriteLine($"resized+recompressed: {Describe(resized)}");
            output.WriteLine($"cropped+brightened:   {Describe(edited)}; ≤8: {edited.Count(d => d <= 8)}, ≤12: {edited.Count(d => d <= 12)}");
            output.WriteLine($"different ≤12: {different.Count(d => d <= 12)}");
            output.WriteLine($"same picture, 2 sizes: {Describe(same)}");
            output.WriteLine($"different pictures:   {Describe(different)}");
            output.WriteLine("closest different pairs: " + string.Join(", ", Enumerable.Range(0, wallpapers.Count)
                .SelectMany(i => Enumerable.Range(i + 1, wallpapers.Count - i - 1).Select(j => (i, j)))
                .Where(p => Picture(wallpapers[p.i]) != Picture(wallpapers[p.j]) && Folder(wallpapers[p.i]) != Folder(wallpapers[p.j]))
                .Select(p => (p, d: PerceptualHash.Distance(prints[wallpapers[p.i]].Hash, prints[wallpapers[p.j]].Hash)))
                .OrderBy(x => x.d).Take(6).Select(x => $"{Path.GetFileName(wallpapers[x.p.i])}~{Path.GetFileName(wallpapers[x.p.j])}={x.d}")));

            Assert.All(resized, d => Assert.True(d <= SimilarityLevel.Strict.MaxDistance(), $"resized copy at {d}"));
            Assert.All(same, d => Assert.True(d <= SimilarityLevel.Strict.MaxDistance(), $"same picture at {d}"));
            // Light edits: most match at "Parecidas", nearly all at "Bem parecidas".
            Assert.True(edited.Count(d => d <= SimilarityLevel.Normal.MaxDistance()) * 2 >= edited.Count, "light edits at Parecidas");
            Assert.True(edited.Count(d => d <= SimilarityLevel.Loose.MaxDistance()) * 10 >= edited.Count * 8, "light edits at Bem parecidas");
            // Different pictures never match at "Parecidas"; "Bem parecidas" may group a rare look-alike.
            Assert.All(different, d => Assert.True(d > SimilarityLevel.Normal.MaxDistance(), $"different pictures at {d}"));
            Assert.True(different.Count(d => d <= SimilarityLevel.Loose.MaxDistance()) * 100 <= different.Count * 2, "look-alikes at Bem parecidas");
        }
        finally { Directory.Delete(dir, true); }
    }
}
