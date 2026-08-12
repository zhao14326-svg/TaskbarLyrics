using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics.Helpers;

/// <summary>
/// Multi-decoder image loader. Primary: WIC (BitmapDecoder), Fallback: ImageSharp (if available).
/// Returns re-encoded PNG bytes for consistent handling.
/// </summary>
public static class ImageDecoder
{
    /// <summary>Decode image file to PNG bytes.</summary>
    public static byte[]? DecodeFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            return DecodeWithWic(File.ReadAllBytes(filePath));
        }
        catch
        {
            return DecodeWithImageSharpFallback(File.ReadAllBytes(filePath));
        }
    }

    /// <summary>Decode raw image bytes to PNG bytes using WIC.</summary>
    public static byte[]? DecodeWithWic(byte[] rawBytes)
    {
        try
        {
            using var ms = new MemoryStream(rawBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            // Re-encode to PNG
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decode raw bytes using SixLabors.ImageSharp (if available as optional dependency).</summary>
    public static byte[]? DecodeWithImageSharpFallback(byte[] rawBytes)
    {
        try
        {
            // Dynamic check for ImageSharp availability
            var imageType = Type.GetType("SixLabors.ImageSharp.Image, SixLabors.ImageSharp");
            if (imageType == null) return null;

            // Image.Load(rawBytes) → SaveAsPng(stream)
            var loadMethod = imageType.GetMethod("Load", [typeof(byte[])]);
            if (loadMethod == null) return null;
            var image = loadMethod.Invoke(null, [rawBytes]);
            if (image == null) return null;

            using var ms = new MemoryStream();
            var saveMethod = image.GetType().GetMethod("SaveAsPng", [typeof(Stream)]);
            saveMethod?.Invoke(image, [ms]);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decode to WPF BitmapSource for pixel-level operations.</summary>
    public static BitmapSource? DecodeToBitmapSource(byte[] rawBytes)
    {
        try
        {
            using var ms = new MemoryStream(rawBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
        catch { return null; }
    }
}
