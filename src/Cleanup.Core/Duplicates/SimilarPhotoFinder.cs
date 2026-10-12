using System.Numerics;

namespace Cleanup.Core.Duplicates;

/// <summary>A decoded image's perceptual hash and its oriented size in pixels.</summary>
/// <param name="Flat">A (nearly) single-color image: its hash is noise, so it is never compared.</param>
public sealed record ImagePrint(ulong Hash, int Width, int Height, bool Flat = false)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>Decodes an image and fingerprints it (Windows: <c>BitmapDecoder</c>); null when it can't be decoded.</summary>
public interface IImageFingerprinter
{
    ImagePrint? Fingerprint(string path);
}

public sealed record SimilarGroup(string Id, IReadOnlyList<WalkedFile> Images);

/// <param name="Groups">Groups of two or more similar photos.</param>
/// <param name="Skipped">Images that couldn't be decoded.</param>
/// <param name="Partial">True when the image cap or a cancellation cut the comparison short.</param>
/// <param name="BestImageIds">Group id → the copy worth keeping (most pixels, then the largest file).</param>
public sealed record SimilarResult(IReadOnlyList<SimilarGroup> Groups, int Skipped, bool Partial,
    IReadOnlyDictionary<string, string> BestImageIds);

/// <summary>
/// Visually similar photos (bursts, edits, resized copies) by perceptual hash (<see cref="PerceptualHash"/>) and
/// union-find. Exact byte duplicates are excluded: they belong to the identical-files tab. Nothing here selects
/// anything for removal.
/// </summary>
public static class SimilarPhotoFinder
{
    public const int ImageLimit = 3_000;
    public const long MinimumSize = 50 * 1024;

    public static IReadOnlySet<string> Extensions { get; } =
        new HashSet<string> { "jpg", "jpeg", "png", "heic", "heif", "tiff", "tif", "gif", "webp" };

    public static SimilarResult Groups(IEnumerable<WalkedFile> images, IReadOnlySet<string> exactDuplicates, int maxDistance,
        IImageFingerprinter fingerprinter, CancellationToken cancellation)
    {
        // Hard links are one photo: comparing them would offer to remove "both" copies of a single file.
        var seenIds = new HashSet<string>();
        var candidates = images.Where(f => !exactDuplicates.Contains(f.Path) && seenIds.Add(f.FileId)).ToList();
        var limited = candidates.Take(ImageLimit).ToList();
        var printed = new List<(WalkedFile File, ImagePrint Print)>();
        var skipped = 0;
        var empty = new Dictionary<string, string>();
        foreach (var file in limited)
        {
            if (cancellation.IsCancellationRequested) return new SimilarResult([], skipped, true, empty);
            ImagePrint? print;
            try { print = fingerprinter.Fingerprint(file.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { print = null; }
            if (print is null) skipped++;
            else if (!print.Flat) printed.Add((file, print));
        }
        var pairs = new List<(int, int)>();
        for (var i = 0; i < printed.Count; i++)
        {
            if (cancellation.IsCancellationRequested) return new SimilarResult([], skipped, true, empty);
            for (var j = i + 1; j < printed.Count; j++)
            {
                if (PerceptualHash.Distance(printed[i].Print.Hash, printed[j].Print.Hash) <= maxDistance) pairs.Add((i, j));
            }
        }
        var best = new Dictionary<string, string>();
        var groups = Cluster(printed.Count, pairs).Select(indices =>
            {
                var members = indices.Select(i => printed[i]).OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();
                var group = new SimilarGroup(members[0].File.Path, members.Select(m => m.File).ToList());
                var keep = members.OrderByDescending(m => m.Print.Pixels).ThenByDescending(m => m.File.Size).First();
                best[group.Id] = keep.File.Id;
                return group;
            })
            .OrderByDescending(g => g.Images.Count).ThenBy(g => g.Id, StringComparer.Ordinal)
            .ToList();
        return new SimilarResult(groups, skipped, candidates.Count > ImageLimit, best);
    }

    /// <summary>Connected components of the "similar" relation (union-find), groups of two or more.</summary>
    internal static List<List<int>> Cluster(int count, IEnumerable<(int A, int B)> pairs)
    {
        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        foreach (var (a, b) in pairs)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }
        return Enumerable.Range(0, count).GroupBy(Find).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
    }
}

/// <summary>
/// How alike photos must be to count as "similar" (Duplicados › Fotos parecidas), as the largest Hamming distance
/// between two 64-bit perceptual hashes. The user picks the level, as on macOS.
/// <para>
/// Calibration (2026-10-12, this PC, Platform test <c>PerceptualHashCalibrationTests</c>, on the 36 pictures in
/// <c>C:\Windows\Web</c>): copies resized to half and re-encoded as JPEG quality 60 were 0–2 bits away (so
/// "Quase idênticas" = 4); crop 4 % + brightness +12 % + resize 2–14 (median 8: 23 of 35 within 8, 32 within 12);
/// different pictures 22–42 (median 32, what random hashes give). A theme's variants (one composition in other
/// colors; the hash only sees luminance) were 2–12 apart: "Bem parecidas" groups those, like bursts. Solid-color
/// pictures are left out (<see cref="PerceptualHash.IsFlat"/>). macOS used Vision feature prints with 0.15 / 0.25 /
/// 0.32; these distances play the same role.
/// </para>
/// </summary>
public enum SimilarityLevel
{
    /// <summary>Near-identical shots only (resized or recompressed copies).</summary>
    Strict,
    Normal,
    /// <summary>Bursts and light edits too; may group a few shots that only look alike.</summary>
    Loose,
}

public static class SimilarityLevels
{
    public static IReadOnlyList<SimilarityLevel> All { get; } = Enum.GetValues<SimilarityLevel>();

    public static int MaxDistance(this SimilarityLevel level) => level switch
    {
        SimilarityLevel.Strict => 4,
        SimilarityLevel.Normal => 8,
        _ => 12,
    };

    public static string Title(this SimilarityLevel level) => level switch
    {
        SimilarityLevel.Strict => "Quase idênticas",
        SimilarityLevel.Normal => "Parecidas",
        _ => "Bem parecidas (inclui sequências)",
    };
}

/// <summary>
/// The 64-bit DCT perceptual hash (pHash): a 32×32 grayscale image → 2-D DCT → the 8×8 lowest frequencies → one bit
/// per coefficient, set when it is above their median. Resizing, recompression and small edits barely change it.
/// </summary>
public static class PerceptualHash
{
    public const int Side = 32;
    private const int Low = 8;

    private static readonly double[,] Cosines = BuildCosines();

    private static double[,] BuildCosines()
    {
        var table = new double[Low, Side];
        for (var u = 0; u < Low; u++)
        {
            for (var x = 0; x < Side; x++) table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Side));
        }
        return table;
    }

    /// <summary>Hash of a 32×32 luminance image, row by row (<see cref="Side"/>² values).</summary>
    public static ulong Compute(ReadOnlySpan<byte> luminance)
    {
        if (luminance.Length != Side * Side) throw new ArgumentException($"expected {Side}×{Side} pixels", nameof(luminance));
        // Rows first (only the 8 lowest frequencies are needed), then columns.
        var rows = new double[Side, Low];
        for (var y = 0; y < Side; y++)
        {
            for (var u = 0; u < Low; u++)
            {
                double sum = 0;
                for (var x = 0; x < Side; x++) sum += luminance[y * Side + x] * Cosines[u, x];
                rows[y, u] = sum;
            }
        }
        var coefficients = new double[Low * Low];
        for (var v = 0; v < Low; v++)
        {
            for (var u = 0; u < Low; u++)
            {
                double sum = 0;
                for (var y = 0; y < Side; y++) sum += rows[y, u] * Cosines[v, y];
                coefficients[v * Low + u] = sum;
            }
        }
        var sorted = (double[])coefficients.Clone();
        Array.Sort(sorted);
        var median = (sorted[31] + sorted[32]) / 2;
        ulong hash = 0;
        for (var i = 0; i < coefficients.Length; i++)
        {
            if (coefficients[i] > median) hash |= 1UL << i;
        }
        return hash;
    }

    /// <summary>ITU-R BT.601 luma of BGRA pixels (the layout <c>BitmapDecoder</c> returns).</summary>
    public static byte[] LuminanceFromBgra(ReadOnlySpan<byte> bgra)
    {
        var result = new byte[bgra.Length / 4];
        for (var i = 0; i < result.Length; i++)
        {
            var b = bgra[i * 4];
            var g = bgra[i * 4 + 1];
            var r = bgra[i * 4 + 2];
            result[i] = (byte)Math.Clamp((int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
        }
        return result;
    }

    /// <summary>
    /// True when the luminance barely varies (standard deviation under 3 of 255): solid backgrounds and blank scans,
    /// whose hash bits only reflect JPEG noise (a solid blue wallpaper moved 21 bits after a resize).
    /// </summary>
    public static bool IsFlat(ReadOnlySpan<byte> luminance)
    {
        if (luminance.IsEmpty) return true;
        double sum = 0, squares = 0;
        foreach (var v in luminance)
        {
            sum += v;
            squares += (double)v * v;
        }
        var mean = sum / luminance.Length;
        return squares / luminance.Length - mean * mean < 9;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
