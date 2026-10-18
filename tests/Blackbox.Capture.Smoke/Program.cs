using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Blackbox.Capture.Protocol;
using Blackbox.Capture;
using Blackbox.Domain;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Blackbox.Capture.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--sync", var worker, var ffmpeg, var output, var seconds])
            return AudioVideoSyncSmoke.Run(worker, ffmpeg, output, int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture));
        if (args is ["--benchmark", var configuration]) return RecorderBenchmark.Run(configuration);
        if (args is ["--benchmark-microphone"])
        {
            return BenchmarkMicrophone();
        }
        var window = new Window { Title = "Blackbox capture test pattern", Width = 1280, Height = 720,
            Content = new TestPattern(), WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var app = new Application();
        var exitCode = 0;
        window.Loaded += async (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle.ToInt64();
            Console.WriteLine(JsonSerializer.Serialize(new { windowHandle = handle, processId = Environment.ProcessId }));
            if (args.Length < 3) return;
            var extended = args.Skip(3).Contains("--extended", StringComparer.OrdinalIgnoreCase);
            var microphoneOption = Array.FindIndex(args, 3, argument =>
                argument.Equals("--microphone-id", StringComparison.OrdinalIgnoreCase));
            var microphoneId = microphoneOption >= 0 && microphoneOption + 1 < args.Length
                ? args[microphoneOption + 1]
                : null;
            try { await RunAsync(window, handle, args[0], args[1], args[2], extended, microphoneId); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        return exitCode;
    }

    private static int BenchmarkMicrophone()
    {
        var input = new float[BroadcastMicrophoneProcessor.FrameSamples];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = 0.2f * MathF.Sin(2 * MathF.PI * 200 * i / BroadcastMicrophoneProcessor.SampleRate) +
                       0.003f * MathF.Sin(2 * MathF.PI * 75 * i / BroadcastMicrophoneProcessor.SampleRate);
        }

        var frame = new float[input.Length];
        using var processor = new BroadcastMicrophoneProcessor(new MicrophoneProcessingSettings());
        for (var i = 0; i < 500; i++)
        {
            input.CopyTo(frame, 0);
            processor.Process(frame);
        }

        const int frames = 10000;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            input.CopyTo(frame, 0);
            processor.Process(frame);
        }

        clock.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            frames,
            audioSeconds = frames * BroadcastMicrophoneProcessor.FrameSamples / (double)BroadcastMicrophoneProcessor.SampleRate,
            processingMilliseconds = clock.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
            peak = frame.Max(static sample => Math.Abs(sample))
        }));
        return 0;
    }

    private static async Task RunAsync(Window window, long handle, string workerPath, string ffmpegPath,
        string directory, bool extended, string? microphoneId)
    {
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Path.GetFullPath(workerPath)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var worker = Process.Start(start) ?? throw new IOException("Worker launch failed.");
        var errors = Task.Run(async () => { while (await worker.StandardError.ReadLineAsync() is { } line) Console.Error.WriteLine(line); });
        var sequence = 0L;
        async Task<CaptureReply> Send(CaptureCommand command, bool expectSuccess = true)
        {
            command.Id = ++sequence;
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
            await worker.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            while (await worker.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                var reply = JsonSerializer.Deserialize<CaptureReply>(line, CaptureJson.Options);
                if (reply?.Id != command.Id) continue;
                Console.WriteLine(JsonSerializer.Serialize(new { command.Operation, reply.Success, reply.State, reply.Paused, reply.Frames, reply.BytesWritten, reply.DurationSeconds, reply.Error, reply.Diagnostic, reply.SnapshotMeanLuma, reply.SnapshotBlackFraction, reply.MicrophoneCaptureEnabled, reply.MicrophonePacketReceived, reply.MicrophonePacketCount, reply.MicrophoneProcessingEnabled, reply.MicrophonePreset, reply.MicrophoneDroppedFrameCount }));
                if (!reply.Success && expectSuccess) throw new InvalidOperationException(reply.Error);
                return reply;
            }
            throw new EndOfStreamException("Worker exited before replying.");
        }
        try
        {
            using var tone = new WaveOutEvent();
            if (extended) { tone.Init(new SignalGenerator(48000, 2) { Frequency = 440, Gain = .025, Type = SignalGeneratorType.Sin }); tone.Play(); }
            await Send(new() { Operation = "probe" });
            var first = await Send(new() { Operation = "start", Start = new CaptureStartRequest {
                WindowHandle = handle, ProcessId = Environment.ProcessId, OutputDirectory = Path.GetFullPath(directory),
                FilePrefix = $"{DateTime.Now:yyyy-MM-dd HH-mm-ss}-{Guid.NewGuid():N}", FfmpegPath = Path.GetFullPath(ffmpegPath),
                Width = 1280, Height = 720, FramesPerSecond = 60, VideoBitrate = 6_000_000,
                SegmentSeconds = 5, AudioBitrate = 192000, CaptureSystemAudio = extended,
                CaptureMicrophone = microphoneId is not null, MicrophoneId = microphoneId } });
            if (first.Frames <= 0 || first.BytesWritten <= 0) throw new InvalidOperationException("No first-frame evidence.");
            if (microphoneId is not null &&
                (!first.MicrophoneCaptureEnabled || !first.MicrophonePacketReceived || first.MicrophonePacketCount <= 0 ||
                 !first.MicrophoneProcessingEnabled || first.MicrophonePreset != "Broadcast"))
                throw new InvalidOperationException("The requested microphone did not start with verified Broadcast processing.");
            var snapshot = await Send(new() { Operation = "snapshot" });
            if (snapshot.SnapshotPngBase64 is null || snapshot.SnapshotMeanLuma < .1) throw new InvalidOperationException("Live preview is empty/black.");
            await File.WriteAllBytesAsync(Path.Combine(directory, "preview.png"), Convert.FromBase64String(snapshot.SnapshotPngBase64));
            if (microphoneId is not null)
            {
                var changedMicrophone = await Send(new()
                {
                    Operation = "microphone",
                    MicrophoneId = microphoneId,
                    MicrophoneGainDb = 0,
                    MicrophoneProcessing = new CaptureMicrophoneProcessing
                    {
                        Preset = "StrongNoiseRemoval",
                        NoiseSuppressionDb = -32,
                        HighPassCutoffHz = 90,
                        ExpanderThresholdDb = -40,
                        ExpanderRatio = 4,
                        LowMidEqDb = -3,
                        PresenceEqDb = 2.5,
                        CompressorThresholdDb = -20,
                        CompressorRatio = 3.5,
                        DeEsserThresholdDb = -22
                    }
                });
                if (!changedMicrophone.MicrophoneProcessingEnabled ||
                    changedMicrophone.MicrophonePreset != "StrongNoiseRemoval")
                    throw new InvalidOperationException("Live microphone preset change was not applied.");
            }
            var clock = Stopwatch.StartNew();
            worker.Refresh();
            var cpu = worker.TotalProcessorTime;
            await Task.Delay(7000);
            window.Width = 900; window.Height = 900;
            await Task.Delay(6000);
            window.WindowState = WindowState.Minimized;
            await Task.Delay(3500);
            var paused = await Send(new() { Operation = "status" });
            if (!paused.Paused) throw new InvalidOperationException("Minimized target was not paused.");
            if (paused.MicrophoneDroppedFrameCount != 0)
                throw new InvalidOperationException($"Microphone processing dropped {paused.MicrophoneDroppedFrameCount} frame(s).");
            window.WindowState = WindowState.Normal;
            await Task.Delay(5000);
            var last = await Send(new() { Operation = "stop" });
            if (last.Frames <= first.Frames || last.BytesWritten <= first.BytesWritten || !File.Exists(last.OutputPath))
                throw new InvalidOperationException("Recording did not produce a finalized file.");
            worker.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", seconds = clock.Elapsed.TotalSeconds,
                workerCpuOneCorePercent = (worker.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds * 100,
                last.Frames, last.BytesWritten, last.OutputPath }));
            tone.Stop();
            if (extended)
            {
                var second = new Window { Title = "Blackbox second capture target", Width = 800, Height = 600, Content = new TestPattern() };
                second.Show();
                var secondHandle = new WindowInteropHelper(second).Handle.ToInt64();
                CaptureCommand NewSession() => new() { Operation = "start", Start = new CaptureStartRequest {
                    WindowHandle = handle, ProcessId = Environment.ProcessId, OutputDirectory = Path.GetFullPath(directory),
                    FilePrefix = $"{DateTime.Now:yyyy-MM-dd HH-mm-ss}-{Guid.NewGuid():N}", FfmpegPath = Path.GetFullPath(ffmpegPath),
                    Width = 1280, Height = 720, FramesPerSecond = 30, SegmentSeconds = 5,
                    CaptureSystemAudio = false, CaptureMicrophone = microphoneId is not null,
                    MicrophoneId = microphoneId } };
                await Send(NewSession());
                window.WindowState = WindowState.Minimized;
                await Task.Delay(1500);
                var pausedBeforeSwitch = await Send(new() { Operation = "status" });
                if (!pausedBeforeSwitch.Paused) throw new InvalidOperationException("Target did not pause before switching windows.");
                var changed = await Send(new() { Operation = "target", WindowHandle = secondHandle, ProcessId = Environment.ProcessId });
                if (changed.WindowHandle != secondHandle) throw new InvalidOperationException("Target switch retained the wrong HWND.");
                await Task.Delay(2000);
                var switched = await Send(new() { Operation = "status" });
                if (switched.Paused || microphoneId is not null && switched.MicrophonePacketCount <= pausedBeforeSwitch.MicrophonePacketCount)
                    throw new InvalidOperationException("Target switch did not resume video and microphone capture.");
                second.Close();
                await Task.Delay(6500);
                var closed = await Send(new() { Operation = "status" });
                if (!closed.Success || closed.State != "stopped" || closed.Diagnostic != "target_closed" || !File.Exists(closed.OutputPath))
                    throw new InvalidOperationException("Closed target did not finalize safely.");
                window.WindowState = WindowState.Normal;
                await Send(NewSession());
                await Task.Delay(2000);
                worker.StandardInput.Close();
                var remaining = worker.StandardOutput.ReadToEndAsync();
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                var finalReplies = await remaining;
                if (worker.ExitCode != 0) throw new InvalidOperationException("Parent disconnect did not finalize normally: " + finalReplies);
                Console.WriteLine("PASS: system audio, repeated start, live target switch, target closure, restart, and parent disconnect.");
                return;
            }
            await Send(new() { Operation = "shutdown" });
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await errors;
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
        }
    }
}

internal sealed class TestPattern : FrameworkElement
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private static readonly Brush[] Colors = [Brushes.White, Brushes.Gold, Brushes.Cyan, Brushes.LimeGreen, Brushes.Magenta, Brushes.Red, Brushes.RoyalBlue];

    public TestPattern()
    {
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        for (var i = 0; i < Colors.Length; i++)
            drawing.DrawRectangle(Colors[i], null, new Rect(i * ActualWidth / Colors.Length, 0, ActualWidth / Colors.Length + 1, ActualHeight));
        var x = (_clock.Elapsed.TotalSeconds * 200) % Math.Max(1, ActualWidth - 80);
        drawing.DrawRectangle(Brushes.Black, null, new Rect(x, ActualHeight * .4, 80, 100));
        var caption = new FormattedText($"BLACKBOX NATIVE CAPTURE TEST  {_clock.Elapsed.TotalSeconds:F2}s\n{ActualWidth:F0} x {ActualHeight:F0}",
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 26, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawRectangle(Brushes.White, null, new Rect(12, 12, Math.Min(650, ActualWidth - 24), 90));
        drawing.DrawText(caption, new Point(24, 24));
    }
}
