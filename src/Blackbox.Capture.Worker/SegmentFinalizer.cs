using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Blackbox.Capture.Worker;

internal sealed class SegmentFinalizer(string directory, string prefix)
{
    private readonly DateTime sessionStart = prefix.Length >= 19 && DateTime.TryParseExact(prefix[..19],
        "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart) ? parsedStart : DateTime.Now;
    private readonly StringBuilder pending = new();
    private readonly byte[] buffer = new byte[4096];
    private long offset;
    public string CsvPath { get; } = Path.Combine(directory, prefix + ".segments.csv");
    public long FinalizedBytes { get; private set; }
    public int NextIndex { get; private set; }
    public string? LastFinalizedPath { get; private set; }

    public void Poll()
    {
        if (!File.Exists(CsvPath)) return;
        using var stream = new FileStream(CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < offset) throw new IOException("FFmpeg segment CSV was truncated during capture.");
        if (stream.Length == offset) return;
        stream.Seek(offset, SeekOrigin.Begin);
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            offset += count;
            // FFmpeg emits the ASCII FilePrefix basename; defer partial CSV rows until LF.
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    FinalizeRow(pending.ToString().TrimEnd('\r'));
                    pending.Clear();
                }
                else pending.Append((char)buffer[i]);
                if (pending.Length > 16_384) throw new IOException("FFmpeg segment CSV record exceeded its size limit.");
            }
        }
    }

    private void FinalizeRow(string row)
    {
        using var parser = new TextFieldParser(new StringReader(row)) { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var fields = parser.ReadFields();
        if (fields is not { Length: 3 } ||
            !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
            !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var end) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start)
            throw new IOException("Invalid FFmpeg closed-segment CSV record.");
        var name = Path.GetFileName(fields[0]);
        if (!name.StartsWith(prefix + "-", StringComparison.Ordinal) || !name.EndsWith(".active.mkv", StringComparison.Ordinal))
            throw new IOException("Unexpected filename in FFmpeg segment CSV.");
        var number = name.AsSpan(prefix.Length + 1, name.Length - prefix.Length - 1 - ".active.mkv".Length);
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index != NextIndex)
            throw new IOException("Invalid segment index in FFmpeg segment CSV.");
        var active = Path.Combine(directory, name);
        var finalized = Path.Combine(directory, name[..^".active.mkv".Length] + ".mkv");
        if (!File.Exists(active))
        {
            if (!File.Exists(finalized)) throw new IOException("Closed segment is missing: " + name);
        }
        else
        {
            // A CSV row is emitted only after the segment trailer is written and the file closed.
            File.SetCreationTime(active, sessionStart.AddSeconds(start));
            File.Move(active, finalized, overwrite: false);
        }
        FinalizedBytes += new FileInfo(finalized).Length;
        LastFinalizedPath = finalized;
        NextIndex = checked(index + 1);
    }
}
