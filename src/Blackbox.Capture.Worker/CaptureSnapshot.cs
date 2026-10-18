using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blackbox.Capture.Protocol;

namespace Blackbox.Capture.Worker;

internal static class CaptureSnapshot
{
    public static void FillReply(Stream png, CaptureReply reply)
    {
        png.Position = 0;
        var decoder = BitmapDecoder.Create(png, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var ratio = Math.Min(1, Math.Min(320.0 / frame.PixelWidth, 180.0 / frame.PixelHeight));
        BitmapSource small = new TransformedBitmap(frame, new ScaleTransform(ratio, ratio));
        small = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[small.PixelWidth * small.PixelHeight * 4];
        small.CopyPixels(pixels, small.PixelWidth * 4, 0);
        double total = 0;
        var black = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luma = (0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i]) / 255;
            total += luma;
            if (luma < 0.025) black++;
        }
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(small));
        encoder.Save(output);
        if (output.Length > 256 * 1024) throw new IOException("Downscaled snapshot exceeded 256 KiB.");
        reply.SnapshotPngBase64 = Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length));
        reply.SnapshotWidth = small.PixelWidth;
        reply.SnapshotHeight = small.PixelHeight;
        reply.SnapshotMeanLuma = total / (pixels.Length / 4);
        reply.SnapshotBlackFraction = (double)black / (pixels.Length / 4);
        reply.Evidence += "; snapshot=native current capture texture (not decoded encoded video), max 320x180 PNG";
    }
}

internal sealed class BoundedSnapshotStream : MemoryStream
{
    private const int MaximumBytes = 40 * 1024 * 1024;
    public bool Overflowed { get; private set; }
    public override bool CanWrite => !Overflowed && base.CanWrite;
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Position + count > MaximumBytes) { Overflowed = true; return; }
        base.Write(buffer, offset, count);
    }
    public override void SetLength(long value)
    {
        if (value > MaximumBytes) { Overflowed = true; return; }
        base.SetLength(value);
    }
}
