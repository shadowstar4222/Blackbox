using System.Buffers.Binary;
using Blackbox.Capture.Worker;

namespace Blackbox.Capture.Tests;

public sealed class EncodedPipeStreamTests
{
    [Fact]
    public async Task Supports_position_queries_and_final_header_backpatches_without_corrupting_media()
    {
        using var destination = new MemoryStream();
        using var stream = new EncodedPipeStream(destination);
        var original = Box("ftyp", 8).Concat(Box("moov", 80)).Concat(Box("moof", 8)).Concat(Box("mdat", 80)).ToArray();
        foreach (var value in original) stream.Write([value], 0, 1);
        Assert.Equal(original.Length, stream.Seek(0, SeekOrigin.Current));
        stream.BeginFinalization();
        Assert.Equal(20, stream.Seek(20, SeekOrigin.Begin));
        stream.Write([1, 2, 3, 4], 0, 4);
        Assert.Equal(original.Length, stream.Seek(0, SeekOrigin.End));
        await stream.CompleteAsync();
        Assert.Null(stream.Failure);
        Assert.Equal(original, destination.ToArray());
    }

    [Fact]
    public async Task Rejects_backpatches_during_capture()
    {
        using var destination = new MemoryStream();
        using var stream = new EncodedPipeStream(destination);
        var bytes = Box("ftyp", 8).Concat(Box("moof", 8)).Concat(Box("mdat", 80)).ToArray();
        stream.Write(bytes, 0, bytes.Length);
        stream.Seek(0, SeekOrigin.Begin);
        await stream.CompleteAsync();
        Assert.Contains("Unsupported", stream.Failure);
    }

    [Fact]
    public void Malformed_initialization_header_fails_boundedly()
    {
        using var destination = new MemoryStream();
        using var stream = new EncodedPipeStream(destination);
        stream.Write(new byte[8], 0, 8);
        Assert.NotNull(stream.Failure);
    }

    [Fact]
    public async Task Backpressure_preserves_every_chunk_and_returns_all_bytes_in_order()
    {
        using var destination = new GatedStream();
        using var stream = new EncodedPipeStream(destination);
        var data = Box("ftyp", 8).Concat(Box("moof", 8))
            .Concat(Enumerable.Range(0, 80 * 64 * 1024).Select(static index => (byte)(index % 251))).ToArray();
        var producer = Task.Run(() => stream.Write(data, 0, data.Length));
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
        Assert.False(producer.IsCompleted);
        destination.Release.TrySetResult();
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        await stream.CompleteAsync();
        Assert.Null(stream.Failure);
        Assert.Equal(data.Length, stream.BytesWritten);
        Assert.Equal(data, destination.ToArray());
    }

    [Fact]
    public async Task Aborting_a_full_queue_unblocks_the_native_writer_without_throwing()
    {
        using var destination = new GatedStream();
        using var stream = new EncodedPipeStream(destination);
        var data = Box("ftyp", 8).Concat(Box("moof", 8)).Concat(new byte[80 * 64 * 1024]).ToArray();
        var producer = Task.Run(() => stream.Write(data, 0, data.Length));
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
        stream.Abort();
        await producer.WaitAsync(TimeSpan.FromSeconds(3));
        await stream.CompleteAsync();
        Assert.False(stream.CanWrite);
        Assert.NotNull(stream.Failure);
    }

    [Fact]
    public async Task Broken_destination_is_reported_instead_of_crossing_the_native_boundary()
    {
        using var destination = new BrokenStream();
        using var stream = new EncodedPipeStream(destination);
        var bytes = Box("ftyp", 8).Concat(Box("moof", 8)).ToArray();
        stream.Write(bytes, 0, bytes.Length);
        await stream.CompleteAsync();
        Assert.Contains("FFmpeg stdin write failed", stream.Failure);
    }

    [Fact]
    public async Task Stalled_destination_hits_its_write_deadline_without_reusing_the_pending_buffer()
    {
        using var destination = new GatedStream();
        using var stream = new EncodedPipeStream(destination);
        var bytes = Box("ftyp", 8).Concat(Box("moof", 8)).ToArray();
        stream.Write(bytes, 0, bytes.Length);
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await stream.CompleteAsync();
        Assert.Contains("FFmpeg stdin write failed", stream.Failure);
        Assert.Equal(0, stream.BytesWritten);
    }

    private sealed class GatedStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Test pipe closed."));
    }

    private static byte[] Box(string name, int length)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(data, length);
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(data, 4);
        return data;
    }
}
