using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blackbox.Infrastructure;
using Blackbox.Recording;

namespace Blackbox.App;

public sealed class WpfRecordingFrameAnalyzer : IRecordingFrameAnalyzer
{
    public RecordingFrameAnalysis Analyze(ObsPreviewFrame frame)
    {
        using var stream = new MemoryStream(frame.ImageBytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var luminanceSum = 0d;
        var darkPixels = 0;
        var pixelCount = width * height;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var luminance = GetLuminance(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            luminanceSum += luminance;
            if (luminance <= 0.04)
            {
                darkPixels++;
            }
        }

        return new RecordingFrameAnalysis(
            width,
            height,
            luminanceSum / pixelCount,
            darkPixels / (double)pixelCount,
            CreateFingerprint(pixels, width, height, stride));
    }

    private static string CreateFingerprint(byte[] pixels, int width, int height, int stride)
    {
        const int columns = 32;
        const int rows = 18;
        var signature = new byte[columns * rows];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            var y = Math.Min(height - 1, (row * height + height / 2) / rows);
            for (var column = 0; column < columns; column++)
            {
                var x = Math.Min(width - 1, (column * width + width / 2) / columns);
                var offset = y * stride + x * 4;
                var luminance = GetLuminance(
                    pixels[offset + 2],
                    pixels[offset + 1],
                    pixels[offset]);
                signature[index++] = (byte)Math.Clamp((int)(luminance * 31), 0, 31);
            }
        }

        return Convert.ToHexString(SHA256.HashData(signature));
    }

    private static double GetLuminance(byte red, byte green, byte blue) =>
        (0.2126 * red + 0.7152 * green + 0.0722 * blue) / 255d;
}
