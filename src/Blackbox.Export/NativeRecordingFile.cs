using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace Blackbox.Export;

internal sealed record NativeRecordingFile(
    string PrefixPath,
    Guid RecordingId,
    DateTimeOffset RecordingStart,
    int SegmentIndex,
    bool IsActive)
{
    public string LockPath => PrefixPath + ".capture.lock";
    public string CsvPath => PrefixPath + ".segments.csv";

    public static NativeRecordingFile? TryParse(string path)
    {
        var name = Path.GetFileName(path);
        var active = name.EndsWith(".active.mkv", StringComparison.OrdinalIgnoreCase);
        if (!name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stem = name[..^(active ? ".active.mkv".Length : ".mkv".Length)];
        var separator = stem.LastIndexOf('-');
        if (separator != 52 || stem.Length < 57 ||
            !DateTime.TryParseExact(stem.AsSpan(0, 19), "yyyy-MM-dd HH-mm-ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var localStart) ||
            stem[19] != '-' ||
            !Guid.TryParseExact(stem.AsSpan(20, 32), "N", out var recordingId) ||
            !int.TryParse(stem.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        return new NativeRecordingFile(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, stem[..separator]),
            recordingId,
            new DateTimeOffset(DateTime.SpecifyKind(localStart, DateTimeKind.Local)),
            index,
            active);
    }

    public IReadOnlyDictionary<int, (TimeSpan Start, TimeSpan Duration)> ReadSegmentRanges()
    {
        var offsets = new Dictionary<int, (TimeSpan Start, TimeSpan Duration)>();
        try
        {
            using var stream = new FileStream(CsvPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var parser = new TextFieldParser(stream);
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                if (fields is not { Length: 3 } ||
                    TryParse(Path.Combine(Path.GetDirectoryName(PrefixPath)!, Path.GetFileName(fields[0]))) is not { } segment ||
                    !segment.PrefixPath.Equals(PrefixPath, StringComparison.OrdinalIgnoreCase) ||
                    !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
                    !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var end) ||
                    !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start ||
                    end > (DateTimeOffset.MaxValue - RecordingStart).TotalSeconds)
                {
                    continue;
                }

                offsets[segment.SegmentIndex] = (TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end - start));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MalformedLineException)
        {
            // A live CSV may end in an incomplete row. Previously closed rows remain usable.
        }

        return offsets;
    }
}
