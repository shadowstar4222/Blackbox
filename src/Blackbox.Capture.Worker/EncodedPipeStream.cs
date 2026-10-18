using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Channels;

namespace Blackbox.Capture.Worker;

// Only compressed MP4 bytes cross this stream. No video textures or bitmaps are read back.
internal sealed class EncodedPipeStream : Stream
{
    private const int ChunkSize = 64 * 1024;
    private readonly Channel<(byte[] Buffer, int Count)> queue = Channel.CreateBounded<(byte[], int)>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource abort = new();
    private readonly Stream destination;
    private readonly Task pump;
    private long acceptedBytes;
    private long position;
    private readonly object streamGate = new();
    private readonly MemoryStream header = new();
    private int parsedHeaderBytes;
    private int? headerLength;
    private bool finalizing;
    private long writtenBytes;
    private string? failure;
    private int completed;

    public EncodedPipeStream(Stream destination)
    {
        this.destination = destination;
        pump = Task.Run(PumpAsync);
    }

    public long BytesWritten => Interlocked.Read(ref writtenBytes);
    public string? Failure => Volatile.Read(ref failure);
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => Volatile.Read(ref completed) == 0 && Failure is null;
    // Media Foundation queries the current position and backpatches duration fields at stop.
    // The header shadow accepts final metadata patches; the live muxer derives duration from samples.
    public override long Length => Interlocked.Read(ref acceptedBytes);
    public override long Position { get { lock (streamGate) return position; } set => Seek(value, SeekOrigin.Begin); }

    public void BeginFinalization() { lock (streamGate) finalizing = true; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (streamGate) WriteCore(buffer, offset, count);
    }

    private void WriteCore(byte[] buffer, int offset, int count)
    {
        if (position != Length)
        {
            if (!finalizing || headerLength is null || position + count > headerLength)
            { SetFailure("Fragmented MP4 attempted to rewrite already-emitted media."); return; }
            header.Position = position;
            header.Write(buffer, offset, count);
            position += count;
            return;
        }
        while (count > 0 && CanWrite)
        {
            var size = Math.Min(count, ChunkSize);
            var chunk = ArrayPool<byte>.Shared.Rent(size);
            Buffer.BlockCopy(buffer, offset, chunk, 0, size);
            try
            {
                if (!queue.Writer.TryWrite((chunk, size)))
                {
                    // Only stalled consumers need a deadline allocation on the native writer.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    queue.Writer.WriteAsync((chunk, size), timeout.Token).AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                ArrayPool<byte>.Shared.Return(chunk);
                SetFailure("Encoded output backpressure or closed pipe: " + ex.Message);
                // Do not throw managed exceptions through the native IStream delegate boundary.
                return;
            }
            Interlocked.Add(ref acceptedBytes, size);
            position += size;
            if (headerLength is null) TrackHeader(buffer.AsSpan(offset, size));
            offset += size;
            count -= size;
        }
    }

    private void TrackHeader(ReadOnlySpan<byte> bytes)
    {
        if (header.Length + bytes.Length > 1024 * 1024)
        { SetFailure("Fragmented MP4 initialization header exceeded 1 MiB."); return; }
        header.Position = header.Length;
        header.Write(bytes);
        var data = header.GetBuffer().AsSpan(0, (int)header.Length);
        while (parsedHeaderBytes + 8 <= data.Length)
        {
            var box = data[parsedHeaderBytes..];
            if (box.Slice(4, 4).SequenceEqual("moof"u8)) { headerLength = parsedHeaderBytes; return; }
            long size = BinaryPrimitives.ReadUInt32BigEndian(box);
            if (size == 1)
            {
                if (box.Length < 16) return;
                var largeSize = BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
                if (largeSize > 1024 * 1024) { SetFailure("Invalid fragmented MP4 header size."); return; }
                size = (long)largeSize;
            }
            if (size < 8 || size > 1024 * 1024) { SetFailure("Invalid fragmented MP4 initialization box."); return; }
            if (size > box.Length) return;
            parsedHeaderBytes += (int)size;
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(abort.Token))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await destination.WriteAsync(item.Buffer.AsMemory(0, item.Count), timeout.Token);
                    Interlocked.Add(ref writtenBytes, item.Count);
                }
                finally { ArrayPool<byte>.Shared.Return(item.Buffer); }
            }
            await destination.FlushAsync(abort.Token);
        }
        catch (Exception ex) { SetFailure("FFmpeg stdin write failed: " + ex.Message); }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var item)) ArrayPool<byte>.Shared.Return(item.Buffer);
            try { destination.Dispose(); } catch (IOException) { }
        }
    }

    private void SetFailure(string message) => Interlocked.CompareExchange(ref failure, message, null);

    public async Task CompleteAsync()
    {
        Interlocked.Exchange(ref completed, 1);
        queue.Writer.TryComplete();
        try { await pump.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException) { SetFailure("FFmpeg pipe did not drain within 8 seconds."); Abort(); }
    }

    public void Abort()
    {
        Interlocked.Exchange(ref completed, 1);
        abort.Cancel();
        queue.Writer.TryComplete();
        try { destination.Dispose(); } catch (IOException) { }
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (streamGate)
        {
            var next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => Length + offset };
            if (next == Length || (finalizing && next >= 0 && headerLength is { } end && next <= end)) position = next;
            else SetFailure($"Unsupported fragmented MP4 seek to {next} (current extent {Length}).");
            return position;
        }
    }
    public override void SetLength(long value)
    {
        // A streaming sink may request its current extent; truncation/extension is impossible.
        if (value != Length) SetFailure("Native fragmented MP4 sink requested a seekable output stream.");
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Abort();
        base.Dispose(disposing);
    }
}
