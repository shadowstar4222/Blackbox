using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Blackbox.Capture.Protocol;

namespace Blackbox.Capture.Smoke;

internal sealed record RecorderBenchmarkConfig(string WorkerPath, string FfmpegPath, string OutputDirectory,
    int SampleSeconds = 15, int Repetitions = 2, string? MicrophoneId = null, bool IncludeMicrophone = false,
    bool Stress = false, string? ConcurrentCommand = null, string? ConcurrentArguments = null,
    bool IncludeRuntimeStatistics = false, int CooldownSeconds = 0);

internal static class RecorderBenchmark
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static int Run(string configuration)
    {
        var config = JsonSerializer.Deserialize<RecorderBenchmarkConfig>(File.ReadAllText(configuration), Json)
            ?? throw new InvalidDataException("Missing benchmark configuration.");
        if (config.SampleSeconds is < 5 or > 600 || config.Repetitions is < 1 or > 5 || config.CooldownSeconds is < 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(configuration));
        Directory.CreateDirectory(config.OutputDirectory);
        var window = new Window { Title = "Blackbox local recorder benchmark", Width = 1616, Height = 939,
            Content = new TestPattern(), WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var app = new Application();
        var exit = 0;
        window.Loaded += async (_, _) =>
        {
            try { await RunCasesAsync(window, config); }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        return exit;
    }

    private static async Task RunCasesAsync(Window window, RecorderBenchmarkConfig config)
    {
        var results = new List<object>();
        var cases = config.Stress
            ? new[] { (Name: "1080p60-stress", Width: 1920, Height: 1080, Fps: 60) }
            : new[] { ("720p30", 1280, 720, 30), ("1080p60", 1920, 1080, 60), ("1440p60", 2560, 1440, 60), ("4k30", 3840, 2160, 30) };
        for (var repeat = 0; repeat < config.Repetitions; repeat++)
        foreach (var item in cases)
        {
            var name = $"{item.Item1}-r{repeat + 1}";
            Console.WriteLine("START " + name);
            var result = await RunCaseAsync(window, config, name, item.Item2, item.Item3, item.Item4);
            results.Add(result);
            File.WriteAllText(Path.Combine(config.OutputDirectory, "results.json"), JsonSerializer.Serialize(results, Json));
            Console.WriteLine(JsonSerializer.Serialize(result));
            await Task.Delay(1000);
        }
    }

    private static async Task<object> RunCaseAsync(Window window, RecorderBenchmarkConfig config, string name, int width, int height, int fps)
    {
        var directory = Path.Combine(config.OutputDirectory, name);
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Path.GetFullPath(config.WorkerPath))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var worker = Process.Start(start) ?? throw new IOException("Could not start worker.");
        var errorTask = worker.StandardError.ReadToEndAsync();
        long sequence = 0;
        async Task<CaptureReply> Send(CaptureCommand command)
        {
            command.Id = ++sequence;
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
            await worker.StandardInput.FlushAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            while (await worker.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                var reply = JsonSerializer.Deserialize<CaptureReply>(line, CaptureJson.Options);
                if (reply?.Id != command.Id) continue;
                if (!reply.Success) throw new IOException(reply.Error);
                return reply;
            }
            throw new EndOfStreamException("Worker did not reply.");
        }
        Process? concurrent = null;
        try
        {
            await Send(new() { Operation = "probe" });
            var startup = Stopwatch.StartNew();
            await Send(new() { Operation = "start", Start = new CaptureStartRequest
            {
                WindowHandle = new WindowInteropHelper(window).Handle.ToInt64(), ProcessId = Environment.ProcessId,
                OutputDirectory = Path.GetFullPath(directory), FilePrefix = $"{DateTime.Now:yyyy-MM-dd HH-mm-ss}-{Guid.NewGuid():N}",
                FfmpegPath = Path.GetFullPath(config.FfmpegPath), Width = width, Height = height, FramesPerSecond = fps,
                VideoBitrate = Math.Clamp(width * height * fps / 10, 2_000_000, 50_000_000), SegmentSeconds = 5,
                CaptureSystemAudio = false, CaptureMicrophone = config.IncludeMicrophone, MicrophoneId = config.MicrophoneId
            } });
            startup.Stop();
            await Task.Delay(3000);
            var childId = ProcessBenchmarks.Children(worker.Id).Single(id =>
            {
                using var child = Process.GetProcessById(id);
                return string.Equals(child.ProcessName, Path.GetFileNameWithoutExtension(config.FfmpegPath), StringComparison.OrdinalIgnoreCase);
            });
            using var muxer = Process.GetProcessById(childId);
            using var host = Process.GetCurrentProcess();
            using var gpu = new GpuEngineSampler();
            if (config.ConcurrentCommand is not null)
            {
                var other = new ProcessStartInfo(config.ConcurrentCommand, config.ConcurrentArguments ?? "")
                { UseShellExecute = false, CreateNoWindow = true };
                other.Environment["BLACKBOX_DATA_DIRECTORY"] = Path.Combine(directory, "review-profile");
                concurrent = Process.Start(other);
                await Task.Delay(5000);
            }
            var baseline = await Send(new() { Operation = "status", IncludeRuntimeStatistics = config.IncludeRuntimeStatistics });
            var measured = concurrent is null ? new[] { worker, muxer, host } : new[] { worker, muxer, host, concurrent };
            var previous = measured.Select(ProcessBenchmarks.Read).ToArray();
            var samples = new List<object>();
            var states = new List<object>();
            var timer = Stopwatch.StartNew();
            for (var second = 0; second < config.SampleSeconds; second++)
            {
                await Task.Delay(1000);
                if (config.Stress)
                {
                    if (second == 8) { window.Width = 900; window.Height = 900; }
                    if (second == 18) window.WindowState = WindowState.Minimized;
                    if (second == 21) window.WindowState = WindowState.Normal;
                    if (second == 30) { window.Width = 1616; window.Height = 939; }
                }
                var current = measured.Select((process, index) => process.HasExited ? previous[index] : ProcessBenchmarks.Read(process)).ToArray();
                var metrics = current.Select((value, index) => value.Delta(previous[index])).ToArray();
                previous = current;
                var engines = gpu.Read(measured.Where(static process => !process.HasExited).Select(static process => process.Id).ToArray());
                var state = await Send(new() { Operation = "status", IncludeRuntimeStatistics = config.IncludeRuntimeStatistics });
                if (state.MicrophoneDroppedFrameCount != 0) throw new IOException("Microphone processing lost frames.");
                if (config.Stress && second is 20 or 23) states.Add(new { second, state.Paused, state.Frames, state.Diagnostic });
                if (config.Stress && second == 20 && !state.Paused) throw new IOException("Minimized source did not pause capture.");
                if (config.Stress && second == 23 && state.Paused) throw new IOException("Restored source did not resume capture.");
                samples.Add(new { Seconds = timer.Elapsed.TotalSeconds, Worker = metrics[0], Muxer = metrics[1], SourceHost = metrics[2], Gpu = engines,
                    Review = concurrent is { HasExited: false } ? metrics[3] : null,
                    state.Frames, state.BytesWritten, state.DurationSeconds, state.Paused, state.MicrophonePacketCount, state.MicrophoneDroppedFrameCount,
                    state.RuntimeStatistics });
            }
            var beforeStop = await Send(new() { Operation = "status" });
            if (config.IncludeMicrophone && beforeStop.MicrophonePacketCount <= baseline.MicrophonePacketCount)
                throw new IOException("Microphone packets did not advance during the measurement.");
            var measuredSeconds = timer.Elapsed.TotalSeconds;
            var stopping = Stopwatch.StartNew();
            var last = await Send(new() { Operation = "stop" });
            stopping.Stop();
            object? released = null;
            if (config.CooldownSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(config.CooldownSeconds));
                var status = await Send(new() { Operation = "status", IncludeRuntimeStatistics = config.IncludeRuntimeStatistics });
                var memory = ProcessBenchmarks.Read(worker);
                released = new { Seconds = config.CooldownSeconds, memory.WorkingSet, memory.PrivateBytes, memory.Handles, status.RuntimeStatistics };
            }
            await Send(new() { Operation = "shutdown" });
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (concurrent is not null)
            {
                await concurrent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
                if (concurrent.ExitCode != 0) throw new IOException("Concurrent playback benchmark failed.");
            }
            if (last.Frames <= baseline.Frames || last.BytesWritten <= baseline.BytesWritten) throw new IOException("No capture progress.");
            if (last.MicrophoneDroppedFrameCount != 0) throw new IOException("Microphone processing lost frames.");
            var errors = await errorTask;
            File.WriteAllText(Path.Combine(directory, "worker-stderr.txt"), errors);
            return new { Name = name, Width = width, Height = height, FramesPerSecond = fps, Seconds = measuredSeconds,
                SampleSeconds = config.SampleSeconds, StartupMilliseconds = startup.Elapsed.TotalMilliseconds,
                StopMilliseconds = stopping.Elapsed.TotalMilliseconds, WorkerProcessId = worker.Id, MuxerProcessId = muxer.Id,
                ObservedNativeFps = (beforeStop.Frames - baseline.Frames) / measuredSeconds,
                EncodedTimelineSeconds = beforeStop.DurationSeconds - baseline.DurationSeconds,
                last.Frames, last.BytesWritten, last.Evidence, beforeStop.MicrophonePacketCount, beforeStop.MicrophoneDroppedFrameCount,
                ConfiguredMicrophone = config.IncludeMicrophone, Released = released, States = states, Samples = samples };
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); }
            if (concurrent is not null) { if (!concurrent.HasExited) concurrent.Kill(true); concurrent.Dispose(); }
        }
    }
}
