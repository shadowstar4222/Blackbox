using System.Buffers.Binary;
using System.Text;
using Blackbox.Domain;
using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class MatroskaPlaybackStreamTests
{
    private const uint Ebml = 0x1A45DFA3, Segment = 0x18538067, Info = 0x1549A966;
    private const uint Tracks = 0x1654AE6B, Cluster = 0x1F43B675, Cues = 0x1C53BB6B;

    [Fact]
    public void Combines_sources_into_one_segment_preserving_tracks_and_encoded_bytes()
    {
        using var fixture = new Fixture();
        var firstBlock = Element(0xA3, Enumerable.Repeat((byte)0xA5, 128 * 1024).ToArray());
        var secondBlock = Element(0xA0, Enumerable.Repeat((byte)0x5A, 128 * 1024).ToArray());
        var firstBytes = Mkv([MakeCluster(0, firstBlock)], trackUid: 1);
        var secondBytes = Mkv([MakeCluster(20, secondBlock)], trackUid: 999);
        var first = fixture.Add(firstBytes, 0, 1000);
        var second = fixture.Add(secondBytes, 1000, 2000);
        using var stream = MatroskaPlaybackStream.Create([second, first]);
        var bytes = ReadAll(stream);
        var segment = GetSegment(bytes);
        var children = Children(bytes, segment);
        Assert.True(segment.Unknown);
        Assert.Single(children, node => node.Id == Info);
        Assert.Single(children, node => node.Id == Tracks);
        Assert.Single(children, node => node.Id == Cues);
        Assert.DoesNotContain(children, node => node.Id is 0x114D9B74 or 0x1254C367);
        var clusters = children.Where(node => node.Id == Cluster).ToArray();
        Assert.Equal(2, clusters.Length);
        Assert.Equal(new ulong[] { 0, 1020 }, clusters.Select(node => ClusterTime(bytes, node)));
        Assert.Equal(firstBlock, EncodedTail(bytes, clusters[0]));
        Assert.Equal(secondBlock, EncodedTail(bytes, clusters[1]));
        var firstTracks = Children(firstBytes, GetSegment(firstBytes)).Single(node => node.Id == Tracks);
        var outputTracks = children.Single(node => node.Id == Tracks);
        Assert.Equal(firstBytes[firstTracks.Start..firstTracks.End], bytes[outputTracks.Start..outputTracks.End]);
        var duration = Children(bytes, children.Single(node => node.Id == Info)).Single(node => node.Id == 0x4489);
        Assert.Equal(3000d, BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(duration.Data, 8)));
        Assert.Equal(firstBytes, File.ReadAllBytes(first.FilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(second.FilePath));
        Assert.InRange(stream.IndexedBytesRead, 1, 4096);
        Assert.Equal(bytes.LongLength, stream.Length);
    }

    [Fact]
    public void Timestamp_and_cluster_size_headers_grow_at_integer_boundaries()
    {
        using var fixture = new Fixture();
        var block = Element(0xA3, new byte[121]);
        var first = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [1]))]), 0, 1);
        var second = fixture.Add(Mkv([MakeCluster(255, block)]), 1, 1000);
        using var stream = MatroskaPlaybackStream.Create([first, second]);
        var output = ReadAll(stream);
        var cluster = Children(output, GetSegment(output)).Last(node => node.Id == Cluster);
        var timestamp = Children(output, cluster).First();
        Assert.Equal(256UL, Unsigned(output, timestamp));
        Assert.Equal(2, timestamp.End - timestamp.Data);
        Assert.Equal(6, cluster.Data - cluster.Start); // four-byte ID plus two-byte size
        Assert.Equal(block, EncodedTail(output, cluster));
    }

    [Fact]
    public void Supports_large_timestamp_values_and_rounds_cumulative_offsets_without_drift()
    {
        using var fixture = new Fixture();
        var first = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [1]))], scale: 1), 0, 5000);
        var second = fixture.Add(Mkv([MakeCluster(7, Element(0xA3, [2]))], scale: 1), 5000, 1000);
        using var stream = MatroskaPlaybackStream.Create([first, second]);
        var output = ReadAll(stream);
        var last = Children(output, GetSegment(output)).Last(node => node.Id == Cluster);
        Assert.Equal(5_000_000_007UL, ClusterTime(output, last));

        var a = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [3]))], scale: 3_000_000), 0, 1);
        var b = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [4]))], scale: 3_000_000), 1, 1);
        var c = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [5]))], scale: 3_000_000), 2, 1);
        using var rounded = MatroskaPlaybackStream.Create([a, b, c]);
        output = ReadAll(rounded);
        Assert.Equal(new ulong[] { 0, 0, 1 }, Children(output, GetSegment(output))
            .Where(node => node.Id == Cluster).Select(node => ClusterTime(output, node)));
    }

    [Fact]
    public void Strips_prefix_crc_and_stale_position_metadata_without_changing_blocks()
    {
        using var fixture = new Fixture();
        var block = Element(0xA3, [0x81, 0, 0, 0x80, 0xBF, 0x84, 1, 2, 3, 4]);
        var cluster = Master(Cluster, Element(0xBF, [1, 2, 3, 4]), UInt(0xA7, 456),
            UInt(0xAB, 789), UInt(0xE7, 10), block);
        using var stream = MatroskaPlaybackStream.Create([fixture.Add(Mkv([cluster]), 0, 1000)]);
        var output = ReadAll(stream);
        var result = Children(output, GetSegment(output)).Single(node => node.Id == Cluster);
        Assert.Equal(new uint[] { 0xE7, 0xA3 }, Children(output, result).Select(node => node.Id));
        Assert.Equal(block, EncodedTail(output, result));
    }

    [Fact]
    public void Maps_original_cue_times_tracks_and_positions_to_virtual_cluster_offsets()
    {
        using var fixture = new Fixture();
        var first = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [1]))]), 0, 100);
        var second = fixture.Add(Mkv([MakeCluster(10, Element(0xA3, [2]))], cueTime: 25), 100, 1000);
        using var stream = MatroskaPlaybackStream.Create([first, second]);
        var output = ReadAll(stream);
        var segment = GetSegment(output);
        var children = Children(output, segment);
        var points = Children(output, children.Single(node => node.Id == Cues));
        Assert.Equal(2, points.Count);
        Assert.Equal(new ulong[] { 0, 125 }, points.Select(point =>
            Unsigned(output, Children(output, point).Single(node => node.Id == 0xB3))));
        foreach (var point in points)
        {
            var positions = Children(output, Children(output, point).Single(node => node.Id == 0xB7));
            Assert.Equal(1UL, Unsigned(output, positions.Single(node => node.Id == 0xF7)));
            var position = Unsigned(output, positions.Single(node => node.Id == 0xF1));
            Assert.Equal(Cluster, Parse(output, checked(segment.Data + (int)position), segment.End).Id);
        }
        Assert.True(children.FindIndex(node => node.Id == Cues) < children.FindIndex(node => node.Id == Cluster));
    }

    [Fact]
    public void Builds_coarse_cluster_cues_when_source_cues_are_missing()
    {
        using var fixture = new Fixture();
        using var stream = MatroskaPlaybackStream.Create([fixture.Add(Mkv([
            MakeCluster(0, Element(0xA3, [1])), MakeCluster(30, Element(0xA3, [2]))]), 0, 1000)]);
        var output = ReadAll(stream);
        var cues = Children(output, GetSegment(output)).Single(node => node.Id == Cues);
        Assert.Equal(new ulong[] { 0, 30 }, Children(output, cues).Select(point =>
            Unsigned(output, Children(output, point).Single(node => node.Id == 0xB3))));
    }

    [Theory]
    [InlineData("codec")]
    [InlineData("private")]
    [InlineData("number")]
    [InlineData("type")]
    [InlineData("resolution")]
    [InlineData("audio")]
    [InlineData("scale")]
    public void Rejects_incompatible_source_tracks_or_scales(string difference)
    {
        using var fixture = new Fixture();
        var first = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [1]))]), 0, 1000);
        var second = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [2]))],
            scale: difference == "scale" ? 2_000_000UL : 1_000_000UL, difference: difference), 1000, 1000);
        Assert.Throws<NotSupportedException>(() => MatroskaPlaybackStream.Create([first, second]));
    }

    [Fact]
    public void Prefix_indexing_does_not_visit_or_read_encoded_block_tail()
    {
        using var fixture = new Fixture();
        // The first block is followed by deliberately unparseable bytes. Indexing must
        // treat the media tail as opaque, not walk frame headers or allocate its size.
        var tail = new byte[2 * 1024 * 1024];
        var cluster = Master(Cluster, UInt(0xE7, 0), Element(0xA3, [0x81, 0, 0, 0x80]), tail);
        using var stream = MatroskaPlaybackStream.Create([fixture.Add(Mkv([cluster]), 0, 1000)]);
        Assert.InRange(stream.IndexedBytesRead, 1, 2048);
        var output = ReadAll(stream);
        var result = Children(output, GetSegment(output)).Single(node => node.Id == Cluster);
        Assert.Equal(Join(Element(0xA3, [0x81, 0, 0, 0x80]), tail), EncodedTail(output, result));
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("after-block")]
    [InlineData("duplicate-time")]
    [InlineData("late-crc")]
    [InlineData("bad-crc")]
    [InlineData("unknown-cluster")]
    [InlineData("truncated")]
    [InlineData("bad-vint")]
    [InlineData("oversized-child")]
    [InlineData("missing-time")]
    [InlineData("multiple-segments")]
    public void Rejects_unsupported_or_malformed_structure(string kind)
    {
        using var fixture = new Fixture();
        var block = Element(0xA3, [1, 2]);
        var cluster = kind switch
        {
            "prefix" => Master(Cluster, Element(0xEC, new byte[64 * 1024]), UInt(0xE7, 0), block),
            "after-block" => Master(Cluster, block, UInt(0xE7, 0)),
            "duplicate-time" => Master(Cluster, UInt(0xE7, 0), UInt(0xE7, 1), block),
            "late-crc" => Master(Cluster, UInt(0xE7, 0), Element(0xBF, new byte[4]), block),
            "bad-crc" => Master(Cluster, Element(0xBF, new byte[3]), UInt(0xE7, 0), block),
            "unknown-cluster" => Join(new byte[] { 0x1F, 0x43, 0xB6, 0x75, 0xFF }, UInt(0xE7, 0), block),
            "bad-vint" => Element(Cluster, [0]),
            "oversized-child" => Element(Cluster, [0xE7, 0x88, 0]),
            "missing-time" => Master(Cluster, block),
            _ => MakeCluster(0, block)
        };
        var bytes = Mkv([cluster]);
        if (kind == "truncated") bytes = bytes[..^1];
        if (kind == "multiple-segments") bytes = Join(bytes, Mkv([cluster]));
        var segment = fixture.Add(bytes, 0, 1000);
        Assert.Throws<NotSupportedException>(() => MatroskaPlaybackStream.Create([segment]));
    }

    [Fact]
    public void Read_and_seek_match_a_contiguous_stream_at_every_boundary_and_at_eof()
    {
        using var fixture = new Fixture();
        var first = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, new byte[1000]))]), 0, 1000);
        var second = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, Enumerable.Repeat((byte)7, 1000).ToArray()))]), 1000, 1000);
        using var stream = MatroskaPlaybackStream.Create([first, second]);
        var expected = ReadAll(stream);
        var random = new Random(12);
        for (var i = 0; i < 500; i++)
        {
            var position = random.Next(expected.Length + 40);
            Assert.Equal(position, stream.Seek(position, SeekOrigin.Begin));
            var buffer = new byte[random.Next(1, 200)];
            var read = stream.Read(buffer, 0, buffer.Length);
            var count = Math.Min(buffer.Length, Math.Max(0, expected.Length - position));
            Assert.Equal(count, read);
            Assert.Equal(expected.AsSpan(Math.Min(position, expected.Length), count).ToArray(), buffer[..read]);
        }
        stream.Position = 0;
        using var bytewise = new MemoryStream();
        int next;
        while ((next = stream.ReadByte()) >= 0) bytewise.WriteByte((byte)next);
        Assert.Equal(expected, bytewise.ToArray());
        Assert.Equal(0, stream.Read(Span<byte>.Empty));
        Assert.Equal(0, stream.Read(new byte[10], 0, 10));
        Assert.Equal(stream.Length - 2, stream.Seek(-2, SeekOrigin.End));
        Assert.Equal(stream.Length - 1, stream.Seek(1, SeekOrigin.Current));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(long.MaxValue, SeekOrigin.Current));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)44));
        Assert.Throws<ArgumentException>(() => stream.Read(new byte[2], 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[2], -1, 1));
        Assert.Equal(expected.LongLength, stream.Length);
    }

    [Fact]
    public async Task Stream_is_read_only_cancellable_and_disposal_is_idempotent()
    {
        using var fixture = new Fixture();
        var segment = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [1]))]), 0, 1000);
        var stream = MatroskaPlaybackStream.Create([segment]);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        stream.Flush();
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MatroskaPlaybackStream.Create([segment], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.ReadAsync(new byte[1].AsMemory(), cancellation.Token));
        stream.Dispose();
        stream.Dispose();
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Position);
    }

    [Fact]
    public void Sources_are_opened_lazily_and_only_the_current_source_is_retained()
    {
        using var fixture = new Fixture();
        var first = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [1, 2, 3]))]), 0, 1000);
        var second = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [4, 5, 6]))]), 1000, 1000);
        using var stream = MatroskaPlaybackStream.Create([first, second]);
        using (File.Open(first.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (File.Open(second.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var bytes = ReadAll(stream);
        var clusters = Children(bytes, GetSegment(bytes)).Where(node => node.Id == Cluster).ToArray();
        stream.Position = clusters[0].End - 1;
        Assert.Equal(3, stream.ReadByte());
        using (File.Open(second.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        stream.Position = clusters[1].End - 1;
        Assert.Equal(6, stream.ReadByte());
        using (File.Open(first.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        stream.Dispose();
        using (File.Open(second.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public void Rejects_sources_that_change_after_indexing_and_unfinalized_inputs()
    {
        using var fixture = new Fixture();
        var segment = fixture.Add(Mkv([MakeCluster(0, Element(0xA3, [1]))]), 0, 1000);
        using var stream = MatroskaPlaybackStream.Create([segment]);
        File.WriteAllBytes(segment.FilePath, [1]);
        stream.Position = stream.Length - 1;
        Assert.Throws<IOException>(() => stream.ReadByte());
        Assert.Throws<NotSupportedException>(() => MatroskaPlaybackStream.Create([segment with { IsDamaged = true }]));
        Assert.Throws<NotSupportedException>(() => MatroskaPlaybackStream.Create([segment with { FilePath = "test.active.mkv" }]));
        Assert.Throws<NotSupportedException>(() => MatroskaPlaybackStream.Create([segment with { EndTime = segment.StartTime }]));
        Assert.Throws<ArgumentException>(() => MatroskaPlaybackStream.Create([]));
    }

    private static byte[] ReadAll(Stream stream)
    {
        stream.Position = 0;
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }

    private static byte[] Mkv(byte[][] clusters, ulong scale = 1_000_000, ulong trackUid = 1,
        string? difference = null, ulong? cueTime = null)
    {
        var ebml = Master(Ebml, Element(0x4282, Encoding.ASCII.GetBytes("matroska")), UInt(0x4285, 2));
        var info = Master(Info, UInt(0x2AD7B1, scale));
        var tracks = Master(Tracks,
            Master(0xAE, UInt(0xD7, difference == "number" ? 3UL : 1UL), UInt(0x73C5, trackUid),
                UInt(0x83, difference == "type" ? 2UL : 1UL),
                Element(0x86, Encoding.ASCII.GetBytes(difference == "codec" ? "V_MPEGH/ISO/HEVC" : "V_MPEG4/ISO/AVC")),
                Element(0x63A2, difference == "private" ? new byte[] { 9 } : new byte[] { 1, 2, 3 }),
                Master(0xE0, UInt(0xB0, difference == "resolution" ? 1280UL : 1920UL), UInt(0xBA, 1080))),
            Master(0xAE, UInt(0xD7, 2), UInt(0x73C5, trackUid + 1), UInt(0x83, 2),
                Element(0x86, Encoding.ASCII.GetBytes("A_AAC")), Element(0x63A2, [0x12, 0x10]),
                Master(0xE1, UInt(0x9F, difference == "audio" ? 1UL : 2UL))));
        var cues = Array.Empty<byte>();
        if (cueTime is { } time)
        {
            cues = CueBytes(time, 0);
            cues = CueBytes(time, checked((ulong)(info.Length + tracks.Length + cues.Length)));
        }
        return Join(ebml, Master(Segment, info, tracks, cues, Join(clusters)));
    }

    private static byte[] CueBytes(ulong time, ulong position) => Master(Cues,
        Master(0xBB, UInt(0xB3, time), Master(0xB7, UInt(0xF7, 1), UInt(0xF1, position, 8))));
    private static byte[] MakeCluster(ulong timestamp, byte[] block) => Master(Cluster, UInt(0xE7, timestamp), block);
    private static byte[] Master(uint id, params byte[][] payloads) => Element(id, Join(payloads));
    private static byte[] UInt(uint id, ulong value, int width = 0)
    {
        if (width == 0)
        {
            width = 1;
            for (var rest = value >> 8; rest != 0; rest >>= 8) width++;
        }
        var bytes = new byte[width];
        for (var i = width - 1; i >= 0; i--) { bytes[i] = (byte)value; value >>= 8; }
        return Element(id, bytes);
    }

    private static byte[] Element(uint id, byte[] payload)
    {
        var idBytes = BitConverter.GetBytes(id).Reverse().SkipWhile(value => value == 0).ToArray();
        var width = 1;
        while ((ulong)payload.Length >= (1UL << (width * 7)) - 1) width++;
        var size = new byte[width];
        var value = (ulong)payload.Length;
        for (var i = width - 1; i >= 0; i--) { size[i] = (byte)value; value >>= 8; }
        size[0] |= (byte)(1 << (8 - width));
        return Join(idBytes, size, payload);
    }

    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(static bytes => bytes).ToArray();
    private static Node GetSegment(byte[] bytes)
    {
        var header = Parse(bytes, 0, bytes.Length);
        Assert.Equal(Ebml, header.Id);
        var segment = Parse(bytes, header.End, bytes.Length);
        Assert.Equal(Segment, segment.Id);
        Assert.Equal(bytes.Length, segment.End);
        return segment;
    }

    private static List<Node> Children(byte[] bytes, Node parent)
    {
        var nodes = new List<Node>();
        for (var position = parent.Data; position < parent.End;)
        {
            var node = Parse(bytes, position, parent.End);
            nodes.Add(node);
            position = node.End;
        }
        return nodes;
    }

    private static Node Parse(byte[] bytes, int start, int end)
    {
        var position = start;
        var idWidth = Width(bytes[position]);
        uint id = 0;
        for (var i = 0; i < idWidth; i++) id = (id << 8) | bytes[position++];
        var sizeWidth = Width(bytes[position]);
        ulong size = (ulong)(bytes[position++] & (0xFF >> sizeWidth));
        for (var i = 1; i < sizeWidth; i++) size = (size << 8) | bytes[position++];
        var unknown = size == (1UL << (7 * sizeWidth)) - 1;
        var elementEnd = unknown ? end : checked(position + (int)size);
        Assert.InRange(elementEnd, position, end);
        return new Node(id, start, position, elementEnd, unknown);
    }

    private static int Width(byte first)
    {
        for (var width = 1; width <= 8; width++) if ((first & (1 << (8 - width))) != 0) return width;
        throw new InvalidDataException();
    }

    private static ulong Unsigned(byte[] bytes, Node node)
    {
        ulong value = 0;
        for (var i = node.Data; i < node.End; i++) value = (value << 8) | bytes[i];
        return value;
    }
    private static ulong ClusterTime(byte[] bytes, Node cluster) => Unsigned(bytes, Parse(bytes, cluster.Data, cluster.End));
    private static byte[] EncodedTail(byte[] bytes, Node cluster)
    {
        var timestamp = Parse(bytes, cluster.Data, cluster.End);
        Assert.Equal(0xE7U, timestamp.Id);
        return bytes[timestamp.End..cluster.End];
    }
    private readonly record struct Node(uint Id, int Start, int Data, int End, bool Unknown);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        private int _next;
        public Fixture() => Directory.CreateDirectory(_root);
        public RecordingSegment Add(byte[] bytes, int startMilliseconds, int durationMilliseconds)
        {
            var path = Path.Combine(_root, $"{_next++}.mkv");
            File.WriteAllBytes(path, bytes);
            return TestSegments.Create(path, DateTimeOffset.UnixEpoch.AddMilliseconds(startMilliseconds),
                TimeSpan.FromMilliseconds(durationMilliseconds), bytes.Length);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
