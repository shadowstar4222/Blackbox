using System.Diagnostics;
using System.Text.Json;
using Blackbox.Capture.Worker;

namespace Blackbox.Capture.Tests;

public sealed class CapturePerformanceBenchmarks
{
    [Fact]
    public async Task Measure_compressed_pipe_and_unchanged_segment_polling_when_requested()
    {
        var output = Environment.GetEnvironmentVariable("BLACKBOX_BENCHMARK_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var measurements = new List<object>();
        for (var repetition = 0; repetition < 4; repetition++)
        {
            using var stream = new EncodedPipeStream(Stream.Null);
            byte[] header = [0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                0, 0, 0, 8, (byte)'m', (byte)'o', (byte)'o', (byte)'f'];
            stream.Write(header, 0, header.Length);
            var payload = new byte[256];
            const int writes = 50_000;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var producerAllocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < writes; index++) stream.Write(payload, 0, payload.Length);
            var producerBytes = GC.GetAllocatedBytesForCurrentThread() - producerAllocated;
            await stream.CompleteAsync();
            timer.Stop();
            Assert.Null(stream.Failure);
            Assert.Equal(header.Length + (long)writes * payload.Length, stream.BytesWritten);
            measurements.Add(new { Kind = "CompressedPipe", Repetition = repetition, Writes = writes,
                Milliseconds = timer.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                ProducerAllocatedBytes = producerBytes });
        }
        var directory = Path.Combine(Path.GetTempPath(), "Blackbox-poll-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var finalizer = new SegmentFinalizer(directory, "benchmark");
            File.WriteAllText(finalizer.CsvPath, string.Empty);
            const int polls = 10_000;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < polls; index++) finalizer.Poll();
            measurements.Add(new { Kind = "UnchangedSegmentPoll", Polls = polls,
                Milliseconds = timer.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated });
        }
        finally { Directory.Delete(directory, true); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
    }
}
