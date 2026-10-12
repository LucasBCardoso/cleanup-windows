using Cleanup.Core.Duplicates;
using Windows.Graphics.Imaging;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Decodes an image with WIC (<c>Windows.Graphics.Imaging.BitmapDecoder</c>), scaled down to 32×32 by the decoder
/// itself (JPEGs decode at a fraction of their size) with the EXIF orientation applied, and hashes it with
/// <see cref="PerceptualHash"/>. HEIC needs the HEIF Image Extensions; without it those files count as unreadable.
/// </summary>
public sealed class WindowsImageFingerprinter : IImageFingerprinter
{
    public ImagePrint? Fingerprint(string path)
    {
        try { return FingerprintAsync(path).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException
                                   or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<ImagePrint?> FingerprintAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = PerceptualHash.Side,
            ScaledHeight = PerceptualHash.Side,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var bgra = pixels.DetachPixelData();
        if (bgra.Length != PerceptualHash.Side * PerceptualHash.Side * 4) return null;
        var luminance = PerceptualHash.LuminanceFromBgra(bgra);
        return new ImagePrint(PerceptualHash.Compute(luminance), (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight,
            PerceptualHash.IsFlat(luminance));
    }
}
