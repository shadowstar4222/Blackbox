using System.Buffers.Binary;
using System.Text;
using Blackbox.Domain;

namespace Blackbox.Export;

/// <summary>A seekable, range-backed Matroska file with one continuous Segment.</summary>
/// <remarks>Sources must remain finalized and unchanged until this stream is disposed.
/// Indexing is synchronous; callers should prepare it off the UI thread. Supported
/// Clusters have structural metadata before their media-only block tail (native
/// FFmpeg layout); that tail is not parsed or validated.</remarks>
public sealed class MatroskaPlaybackStream : Stream
{
    private const uint Ebml = 0x1A45DFA3, Segment = 0x18538067, Info = 0x1549A966;
    private const uint Tracks = 0x1654AE6B, Cluster = 0x1F43B675, Timestamp = 0xE7;
    private const uint Cues = 0x1C53BB6B, Crc = 0xBF, Void = 0xEC;
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const int MaxClusterPrefixBytes = 64 * 1024;
    private const int MaxIndexEntries = 1_000_000;
    private const int MaxHeaders = 10_000_000;
    private readonly object _gate = new();
    private readonly Source[] _sources;
    private readonly Piece[] _pieces;
    private readonly long _length;
    private long _position;
    private bool _disposed;
    private FileStream? _file;
    private int _fileIndex = -1;

    private MatroskaPlaybackStream(Source[] sources, Piece[] pieces, long length, long indexedBytesRead)
    {
        _sources = sources;
        _pieces = pieces;
        _length = length;
        IndexedBytesRead = indexedBytesRead;
    }

    /// <summary>Bytes actually read while indexing, excluding subsequent playback reads.</summary>
    public long IndexedBytesRead { get; }

    public static MatroskaPlaybackStream Create(IReadOnlyList<RecordingSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0) throw new ArgumentException("At least one segment is required.", nameof(segments));
        if (segments.Count > MaxIndexEntries) throw Unsupported("Too many source files.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Build(segments, cancellationToken);
        }
        catch (OverflowException exception)
        {
            throw new NotSupportedException("Matroska sizes or timestamps exceed supported bounds.", exception);
        }
    }

    private static MatroskaPlaybackStream Build(IReadOnlyList<RecordingSegment> segments, CancellationToken token)
    {
        var sources = new List<Source>();
        var clusters = new List<ClusterIndex>();
        var cues = new List<Cue>();
        byte[]? trackSignature = null;
        byte[]? firstTracks = null;
        ulong commonScale = 0;
        long totalTicks = 0, indexedBytes = 0;
        var indexEntries = 0;
        foreach (var recording in segments.OrderBy(static segment => segment.StartTime))
        {
            token.ThrowIfCancellationRequested();
            if (recording.IsDamaged || recording.FilePath.EndsWith(".active.mkv", StringComparison.OrdinalIgnoreCase) ||
                recording.FilePath.EndsWith(".partial.mkv", StringComparison.OrdinalIgnoreCase))
                throw Unsupported("Only finalized, undamaged Matroska files are supported.");
            var duration = recording.EndTime - recording.StartTime;
            if (duration <= TimeSpan.Zero) throw Unsupported("Source durations must be positive.");
            var source = new Source(Path.GetFullPath(recording.FilePath));
            using var reader = new Reader(source.Path, token);
            source.Length = reader.Length;
            source.LastWrite = File.GetLastWriteTimeUtc(source.Path);
            var root = reader.Header(0, reader.Length);
            if (root.Id != Ebml || root.Size > 4096) throw Unsupported("Invalid Matroska EBML header.");
            ValidateEbml(reader, root);
            var position = root.End;
            Element segment;
            while (true)
            {
                segment = reader.Header(position, reader.Length, allowUnknown: true);
                if (segment.Id == Segment) break;
                if (segment.Id != Void || segment.Unknown) throw Unsupported("Expected a single Matroska Segment.");
                position = segment.End;
            }
            if (segment.End != reader.Length) throw Unsupported("Multiple or trailing Matroska segments are unsupported.");
            Element? tracks = null, info = null, sourceCues = null;
            var sourceClusters = new List<Element>();
            for (position = segment.Data; position < segment.End;)
            {
                token.ThrowIfCancellationRequested();
                var element = reader.Header(position, segment.End);
                switch (element.Id)
                {
                    case Info:
                        if (info is not null) throw Unsupported("Duplicate Info.");
                        info = element;
                        break;
                    case Tracks:
                        if (tracks is not null) throw Unsupported("Duplicate Tracks.");
                        tracks = element;
                        break;
                    case Cues:
                        if (sourceCues is not null) throw Unsupported("Duplicate Cues.");
                        sourceCues = element;
                        break;
                    case Cluster:
                        Limit(sourceClusters.Count + clusters.Count);
                        sourceClusters.Add(element);
                        break;
                    case Ebml:
                    case Segment:
                        throw Unsupported("Nested or concatenated Matroska segments are unsupported.");
                }
                position = element.End;
            }
            if (info is null || tracks is null || sourceClusters.Count == 0)
                throw Unsupported("Source requires Info, Tracks and finite-sized Clusters.");
            var scale = ReadScale(reader, info.Value);
            var (signature, preferredTrack, trackNumbers) = ReadTracks(reader, tracks.Value);
            if (trackSignature is null)
            {
                trackSignature = signature;
                firstTracks = reader.Bytes(tracks.Value.Start, tracks.Value.End - tracks.Value.Start);
                commonScale = scale;
            }
            else if (commonScale != scale || !trackSignature.AsSpan().SequenceEqual(signature))
                throw Unsupported("Source timestamp scales or codec/track layouts differ.");
            var timeOffset = checked((ulong)decimal.Round((decimal)totalTicks * 100 / commonScale,
                0, MidpointRounding.AwayFromZero));
            var clusterMap = new Dictionary<ulong, int>();
            var firstCluster = clusters.Count;
            foreach (var element in sourceClusters)
            {
                token.ThrowIfCancellationRequested();
                clusterMap.Add(checked((ulong)(element.Start - segment.Data)), clusters.Count);
                var cluster = ReadCluster(reader, element, sources.Count, timeOffset);
                indexEntries = checked(indexEntries + 1 + cluster.Ranges.Length);
                Limit(indexEntries);
                clusters.Add(cluster);
            }
            var cueStart = cues.Count;
            if (sourceCues is { } cueElement)
                ReadCues(reader, cueElement, clusterMap, trackNumbers, timeOffset, cues);
            if (cues.Count == cueStart)
            {
                // Without source keyframe cues this is a coarse cluster index, not a promise
                // that every cluster begins with a random-access video frame.
                for (var i = firstCluster; i < clusters.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Limit(cues.Count);
                    cues.Add(new Cue(clusters[i].Time, preferredTrack, i));
                }
            }
            indexedBytes = checked(indexedBytes + reader.BytesRead);
            sources.Add(source);
            totalTicks = checked(totalTicks + duration.Ticks);
        }

        token.ThrowIfCancellationRequested();
        var ebml = Master(Ebml, UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4),
            UInt(0x42F3, 8), Text(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2));
        var durationBytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(durationBytes, (double)((decimal)totalTicks * 100 / commonScale));
        var infoBytes = Master(Info, UInt(0x2AD7B1, commonScale), Encode(0x4489, durationBytes),
            Text(0x4D80, "Blackbox"), Text(0x5741, "Blackbox"));
        byte[] segmentHeader = [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        var orderedCues = cues.OrderBy(static cue => cue.Time).ThenBy(static cue => cue.Track).ToArray();
        // Cue positions use fixed-width uints, making the Cues size independent of
        // the cluster offsets it precedes. No payload pass or offset fixpoint is needed.
        var cueBytes = BuildCues(orderedCues, clusters, token);
        long clusterPosition = checked(infoBytes.Length + firstTracks!.Length + (long)cueBytes.Length);
        foreach (var cluster in clusters)
        {
            token.ThrowIfCancellationRequested();
            cluster.VirtualPosition = checked((ulong)clusterPosition);
            clusterPosition = checked(clusterPosition + cluster.Length);
        }
        cueBytes = BuildCues(orderedCues, clusters, token);
        var pieces = new List<Piece>();
        long length = 0;
        AddMemory(Join(ebml, segmentHeader, infoBytes, firstTracks, cueBytes));
        foreach (var cluster in clusters)
        {
            token.ThrowIfCancellationRequested();
            AddMemory(cluster.Prefix);
            foreach (var range in cluster.Ranges)
            {
                Limit(pieces.Count);
                pieces.Add(new Piece(length, range.Length, cluster.Source, range.Offset, null));
                length = checked(length + range.Length);
            }
        }
        return new MatroskaPlaybackStream(sources.ToArray(), pieces.ToArray(), length, indexedBytes);

        void AddMemory(byte[] data)
        {
            Limit(pieces.Count);
            pieces.Add(new Piece(length, data.Length, -1, 0, data));
            length = checked(length + data.Length);
        }
    }

    private static void ValidateEbml(Reader reader, Element root)
    {
        var docType = false;
        foreach (var child in reader.Children(root))
        {
            if (child.Id == 0x4282)
                docType = Encoding.ASCII.GetString(reader.Bytes(child.Data, child.Size)) == "matroska";
            if ((child.Id == 0x42F2 && reader.Unsigned(child) > 4) ||
                (child.Id == 0x42F3 && reader.Unsigned(child) > 8) ||
                (child.Id == 0x42F7 && reader.Unsigned(child) > 1) ||
                (child.Id == 0x4285 && reader.Unsigned(child) > 4))
                throw Unsupported("Unsupported EBML/Matroska read version.");
        }
        if (!docType) throw Unsupported("Expected Matroska DocType.");
    }

    private static ulong ReadScale(Reader reader, Element info)
    {
        ulong scale = 1_000_000;
        var seen = false;
        foreach (var child in reader.Children(info))
        {
            if (child.Id != 0x2AD7B1) continue;
            if (seen) throw Unsupported("Duplicate TimestampScale.");
            seen = true;
            scale = reader.Unsigned(child);
        }
        if (scale == 0 || scale > long.MaxValue) throw Unsupported("Invalid TimestampScale.");
        return scale;
    }

    private static (byte[] Signature, ulong PreferredTrack, HashSet<ulong> TrackNumbers) ReadTracks(Reader reader, Element tracks)
    {
        if (tracks.Size > MaxMetadataBytes) throw Unsupported("Tracks metadata exceeds the indexing limit.");
        var entries = new SortedDictionary<ulong, byte[]>();
        var numbers = new HashSet<ulong>();
        ulong preferred = 0, video = 0;
        foreach (var entry in reader.Children(tracks))
        {
            if (entry.Id is Crc or Void) continue;
            if (entry.Id != 0xAE) throw Unsupported("Unsupported Tracks child.");
            ulong number = 0, type = 0;
            var codec = false;
            using var signature = new MemoryStream();
            foreach (var field in reader.Children(entry))
            {
                if (field.Id == 0xD7) number = reader.Unsigned(field);
                if (field.Id == 0x83) type = reader.Unsigned(field);
                if (field.Id == 0x86) codec = field.Size > 0;
                if (field.Id is 0x73C5 or Crc or Void) continue; // TrackUID is segment-local identity only.
                signature.Write(reader.Bytes(field.Start, field.End - field.Start));
            }
            if (number == 0 || number >= (1UL << 56) || type == 0 || !codec || !numbers.Add(number))
                throw Unsupported("Invalid or duplicate track definition.");
            entries.Add(number, signature.ToArray());
            if (preferred == 0) preferred = number;
            if (type == 1 && video == 0) video = number;
        }
        if (entries.Count == 0) throw Unsupported("No tracks.");
        return (Join(entries.Values.Select(static bytes => Encode(0xAE, bytes)).ToArray()),
            video == 0 ? preferred : video, numbers);
    }

    private static ClusterIndex ReadCluster(Reader reader, Element cluster, int source, ulong offset)
    {
        ulong? time = null;
        long run = cluster.Data, kept = 0;
        var ranges = new List<FileRange>();
        for (var position = cluster.Data; position < cluster.End;)
        {
            reader.CheckCancellation();
            if (position - cluster.Data >= MaxClusterPrefixBytes)
                throw Unsupported("Cluster structural prefix exceeds 64 KiB.");
            var child = reader.Header(position, cluster.End);
            if (child.Data - cluster.Data > MaxClusterPrefixBytes)
                throw Unsupported("Cluster structural prefix exceeds 64 KiB.");
            if (child.Id is 0xA3 or 0xA0)
            {
                if (time is null) throw Unsupported("Cluster Timestamp must precede media blocks.");
                break;
            }
            if (child.Id is not (Timestamp or Crc or 0xA7 or 0xAB or Void))
                throw Unsupported("Unsupported Cluster structural prefix.");
            if (child.End - cluster.Data > MaxClusterPrefixBytes)
                throw Unsupported("Cluster structural prefix exceeds 64 KiB.");
            if (child.Id == Timestamp)
            {
                if (time is not null) throw Unsupported("Duplicate Cluster Timestamp.");
                time = checked(reader.Unsigned(child) + offset);
            }
            position = child.End;
            if (child.Id == Void) continue;
            if (child.Id == Crc && child.Start != cluster.Data)
                throw Unsupported("Cluster CRC-32 must be the first child.");
            if (child.Id == Crc && child.Size != 4) throw Unsupported("Invalid Cluster CRC-32.");
            if (child.Start > run)
            {
                Limit(ranges.Count);
                ranges.Add(new FileRange(run, child.Start - run));
                kept = checked(kept + child.Start - run);
            }
            run = child.End;
        }
        if (time is null) throw Unsupported("Cluster Timestamp is missing.");
        if (cluster.End > run)
        {
            ranges.Add(new FileRange(run, cluster.End - run));
            kept = checked(kept + cluster.End - run);
        }
        var timestamp = UInt(Timestamp, time.Value);
        var prefix = Join(Header(Cluster, checked((ulong)(kept + timestamp.Length))), timestamp);
        return new ClusterIndex(source, time.Value, prefix, ranges.ToArray(), checked(prefix.Length + kept));
    }

    private static void ReadCues(Reader reader, Element cues, Dictionary<ulong, int> clusterMap,
        HashSet<ulong> tracks, ulong offset, List<Cue> result)
    {
        foreach (var point in reader.Children(cues))
        {
            if (point.Id != 0xBB) continue;
            ulong? time = null;
            var positions = new List<(ulong Track, ulong Position)>();
            foreach (var field in reader.Children(point))
            {
                if (field.Id == 0xB3) time = reader.Unsigned(field);
                if (field.Id != 0xB7) continue;
                ulong track = 0;
                ulong? position = null;
                foreach (var location in reader.Children(field))
                {
                    if (location.Id == 0xF7) track = reader.Unsigned(location);
                    if (location.Id == 0xF1) position = reader.Unsigned(location);
                }
                if (position.HasValue && tracks.Contains(track))
                {
                    Limit(positions.Count);
                    positions.Add((track, position.Value));
                }
            }
            if (!time.HasValue) continue;
            foreach (var position in positions)
            {
                if (!clusterMap.TryGetValue(position.Position, out var cluster)) continue;
                Limit(result.Count);
                result.Add(new Cue(checked(time.Value + offset), position.Track, cluster));
            }
        }
    }

    private static byte[] BuildCues(Cue[] cues, List<ClusterIndex> clusters, CancellationToken token)
    {
        using var payload = new MemoryStream();
        foreach (var cue in cues)
        {
            token.ThrowIfCancellationRequested();
            payload.Write(Master(0xBB, UInt(0xB3, cue.Time, 8),
                Master(0xB7, UInt(0xF7, cue.Track, 8), UInt(0xF1, clusters[cue.Cluster].VirtualPosition, 8))));
        }
        return Encode(Cues, payload.ToArray());
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { lock (_gate) { ThrowIfDisposed(); return _length; } } }
    public override long Position
    {
        get { lock (_gate) { ThrowIfDisposed(); return _position; } }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Invalid buffer range.");
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (buffer.IsEmpty || _position >= _length) return 0;
            var index = FindPiece(_position);
            var total = 0;
            while (!buffer.IsEmpty && _position < _length)
            {
                var piece = _pieces[index++];
                var within = _position - piece.Start;
                var count = (int)Math.Min(buffer.Length, piece.Length - within);
                if (piece.Data is { } data)
                    data.AsSpan(checked((int)within), count).CopyTo(buffer);
                else
                {
                    var file = OpenSource(piece.Source);
                    file.Position = checked(piece.Offset + within);
                    file.ReadExactly(buffer[..count]);
                }
                _position += count;
                total += count;
                buffer = buffer[count..];
            }
            return total;
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            long position;
            try
            {
                position = checked((origin switch
                {
                    SeekOrigin.Begin => 0,
                    SeekOrigin.Current => _position,
                    SeekOrigin.End => _length,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                }) + offset);
            }
            catch (OverflowException exception) { throw new IOException("Seek exceeds stream bounds.", exception); }
            if (position < 0) throw new IOException("Cannot seek before the beginning.");
            _position = position;
            return position;
        }
    }

    private int FindPiece(long position)
    {
        var low = 0;
        var high = _pieces.Length - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (_pieces[middle].Start <= position) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    private FileStream OpenSource(int index)
    {
        if (_fileIndex == index) return _file!;
        _file?.Dispose();
        _file = null;
        _fileIndex = -1;
        var source = _sources[index];
        var file = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        if (file.Length != source.Length || File.GetLastWriteTimeUtc(source.Path) != source.LastWrite)
        {
            file.Dispose();
            throw new IOException("A source Matroska file changed after indexing.");
        }
        _file = file;
        _fileIndex = index;
        return file;
    }

    public override void Flush() { lock (_gate) { ThrowIfDisposed(); } }
    public override void SetLength(long value) => throw new NotSupportedException("Read-only stream.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read-only stream.");
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("Read-only stream.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                _file?.Dispose();
                _file = null;
                _fileIndex = -1;
            }
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static NotSupportedException Unsupported(string message) => new(message);
    private static void Limit(int count)
    {
        if (count >= MaxIndexEntries) throw Unsupported("Matroska index exceeds the metadata limit.");
    }

    private static byte[] Master(uint id, params byte[][] children) => Encode(id, Join(children));
    private static byte[] Text(uint id, string value) => Encode(id, Encoding.UTF8.GetBytes(value));
    private static byte[] UInt(uint id, ulong value, int width = 0)
    {
        if (width == 0)
        {
            width = 1;
            for (var rest = value >> 8; rest != 0; rest >>= 8) width++;
        }
        var bytes = new byte[width];
        for (var i = width - 1; i >= 0; i--) { bytes[i] = (byte)value; value >>= 8; }
        return Encode(id, bytes);
    }

    private static byte[] Encode(uint id, byte[] payload) => Join(Header(id, checked((ulong)payload.Length)), payload);
    private static byte[] Header(uint id, ulong size)
    {
        var idWidth = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        var sizeWidth = 1;
        while (sizeWidth < 8 && size >= (1UL << (7 * sizeWidth)) - 1) sizeWidth++;
        if (size >= (1UL << 56) - 1) throw Unsupported("Element exceeds EBML size bounds.");
        var bytes = new byte[idWidth + sizeWidth];
        for (var i = idWidth - 1; i >= 0; i--) { bytes[i] = (byte)id; id >>= 8; }
        for (var i = bytes.Length - 1; i >= idWidth; i--) { bytes[i] = (byte)size; size >>= 8; }
        bytes[idWidth] |= (byte)(1 << (8 - sizeWidth));
        return bytes;
    }

    private static byte[] Join(params byte[][] arrays)
    {
        var length = 0;
        foreach (var array in arrays) length = checked(length + array.Length);
        var result = new byte[length];
        var position = 0;
        foreach (var array in arrays) { array.CopyTo(result, position); position += array.Length; }
        return result;
    }

    private sealed class Reader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly CancellationToken _token;
        private int _headers;
        public long Length => _stream.Length;
        public long BytesRead { get; private set; }

        public void CheckCancellation() => _token.ThrowIfCancellationRequested();

        public Reader(string path, CancellationToken token)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            _token = token;
        }

        private void ReadAt(long offset, Span<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var read = RandomAccess.Read(_stream.SafeFileHandle, bytes, offset);
                if (read == 0) throw Unsupported("Truncated Matroska element.");
                BytesRead = checked(BytesRead + read);
                bytes = bytes[read..];
                offset += read;
            }
        }

        public Element Header(long position, long end, bool allowUnknown = false)
        {
            _token.ThrowIfCancellationRequested();
            if (++_headers > MaxHeaders) throw Unsupported("Too many EBML headers.");
            if (position < 0 || position >= end || end > Length) throw Unsupported("Truncated EBML header.");
            Span<byte> bytes = stackalloc byte[8];
            ReadAt(position, bytes[..1]);
            var idWidth = VintWidth(bytes[0], 4);
            if (idWidth > end - position) throw Unsupported("Truncated EBML ID.");
            if (idWidth > 1) ReadAt(position + 1, bytes.Slice(1, idWidth - 1));
            uint id = 0;
            for (var i = 0; i < idWidth; i++) id = (id << 8) | bytes[i];
            var sizePosition = position + idWidth;
            if (sizePosition >= end) throw Unsupported("Missing EBML size.");
            ReadAt(sizePosition, bytes[..1]);
            var sizeWidth = VintWidth(bytes[0], 8);
            if (sizeWidth > end - sizePosition) throw Unsupported("Truncated EBML size.");
            if (sizeWidth > 1) ReadAt(sizePosition + 1, bytes.Slice(1, sizeWidth - 1));
            ulong size = (ulong)(bytes[0] & (0xFF >> sizeWidth));
            for (var i = 1; i < sizeWidth; i++) size = (size << 8) | bytes[i];
            var unknown = size == (1UL << (7 * sizeWidth)) - 1;
            var data = sizePosition + sizeWidth;
            if (unknown && !allowUnknown) throw Unsupported("Unknown-sized elements/Clusters are unsupported.");
            if (!unknown && size > (ulong)(end - data)) throw Unsupported("EBML element exceeds its parent or file.");
            return new Element(id, position, data, unknown ? end : data + (long)size, unknown);
        }

        public IEnumerable<Element> Children(Element parent)
        {
            for (var position = parent.Data; position < parent.End;)
            {
                var child = Header(position, parent.End);
                yield return child;
                position = child.End;
            }
        }

        public byte[] Bytes(long position, long size)
        {
            if (size < 0 || size > MaxMetadataBytes || position < 0 || size > Length - position)
                throw Unsupported("EBML metadata exceeds bounds.");
            var bytes = new byte[(int)size];
            ReadAt(position, bytes);
            return bytes;
        }

        public ulong Unsigned(Element element)
        {
            if (element.Size is < 1 or > 8) throw Unsupported("Invalid EBML unsigned integer width.");
            Span<byte> bytes = stackalloc byte[8];
            ReadAt(element.Data, bytes[..(int)element.Size]);
            ulong value = 0;
            for (var i = 0; i < element.Size; i++) value = (value << 8) | bytes[i];
            return value;
        }

        private static int VintWidth(byte first, int maximum)
        {
            for (var width = 1; width <= maximum; width++)
                if ((first & (1 << (8 - width))) != 0) return width;
            throw Unsupported("Invalid EBML variable-length integer.");
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class Source(string path)
    {
        public string Path { get; } = path;
        public long Length { get; set; }
        public DateTime LastWrite { get; set; }
    }

    private readonly record struct Element(uint Id, long Start, long Data, long End, bool Unknown)
    {
        public long Size => End - Data;
    }
    private readonly record struct FileRange(long Offset, long Length);
    private readonly record struct Piece(long Start, long Length, int Source, long Offset, byte[]? Data);
    private readonly record struct Cue(ulong Time, ulong Track, int Cluster);
    private sealed class ClusterIndex(int source, ulong time, byte[] prefix, FileRange[] ranges, long length)
    {
        public int Source { get; } = source;
        public ulong Time { get; } = time;
        public byte[] Prefix { get; } = prefix;
        public FileRange[] Ranges { get; } = ranges;
        public long Length { get; } = length;
        public ulong VirtualPosition { get; set; }
    }
}
