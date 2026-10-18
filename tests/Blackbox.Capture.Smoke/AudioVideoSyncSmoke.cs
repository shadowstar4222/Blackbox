using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Blackbox.Capture.Protocol;
using NAudio.Wave;

namespace Blackbox.Capture.Smoke;

internal static class AudioVideoSyncSmoke
{
    public static int Run(string workerPath, string ffmpegPath, string output, int seconds)
    {
        if (seconds is < 15 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        Directory.CreateDirectory(output);
        var clock = Stopwatch.StartNew();
        var window = new Window { Title = "Blackbox audio/video synchronization test", Width = 800, Height = 500,
            Content = new SyncPattern(clock), WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var app = new Application();
        var exit = 0;
        window.Loaded += async (_, _) =>
        {
            try { await RecordAsync(window, clock, workerPath, ffmpegPath, output, seconds); }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        return exit;
    }

    private static async Task RecordAsync(Window window, Stopwatch clock, string workerPath, string ffmpegPath,
        string output, int seconds)
    {
        using var tone = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
        tone.Init(new SyncTone(clock));
        tone.Play();
        var info = new ProcessStartInfo(Path.GetFullPath(workerPath)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["BLACKBOX_NATIVE_DEBUG"] = "1";
        using var worker = Process.Start(info) ?? throw new IOException("Worker did not start.");
        var errors = worker.StandardError.ReadToEndAsync();
        long sequence = 0;
        async Task<CaptureReply> Send(CaptureCommand command)
        {
            command.Id = ++sequence;
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
            await worker.StandardInput.FlushAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (await worker.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                var reply = JsonSerializer.Deserialize<CaptureReply>(line, CaptureJson.Options);
                if (reply?.Id != command.Id) continue;
                if (command.Operation != "status") Console.WriteLine(line);
                if (!reply.Success) throw new IOException(reply.Error);
                return reply;
            }
            throw new EndOfStreamException();
        }
        try
        {
            await Send(new() { Operation = "start", Start = new CaptureStartRequest
            {
                WindowHandle = new WindowInteropHelper(window).Handle.ToInt64(), ProcessId = Environment.ProcessId,
                OutputDirectory = Path.GetFullPath(output), FfmpegPath = Path.GetFullPath(ffmpegPath),
                FilePrefix = "sync-" + Guid.NewGuid().ToString("N"), Width = 800, Height = 450,
                FramesPerSecond = 30, VideoBitrate = 2_000_000, SegmentSeconds = 30,
                CaptureSystemAudio = true, CaptureMicrophone = true, MicrophoneId = "default"
            } });
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(1000);
                var state = await Send(new() { Operation = "status" });
                if (state.State != "recording") throw new IOException("Capture stopped unexpectedly: " + state.State);
                if (timer.Elapsed.TotalSeconds is >= 35 and < 36) window.WindowState = WindowState.Minimized;
                if (timer.Elapsed.TotalSeconds is >= 39 and < 40) window.WindowState = WindowState.Normal;
            }
            await Send(new() { Operation = "stop" });
            await Send(new() { Operation = "shutdown" });
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (worker.ExitCode != 0) throw new IOException("Worker exited with " + worker.ExitCode);
            Console.WriteLine("SYNC CAPTURE PASS " + output);
        }
        finally
        {
            tone.Stop();
            if (!worker.HasExited) { worker.StandardInput.Close(); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            Console.Error.Write(await errors);
        }
    }

    private sealed class SyncPattern(Stopwatch clock) : FrameworkElement
    {
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            timer.Tick += (_, _) => InvalidateVisual();
            Loaded += (_, _) => timer.Start();
            Unloaded += (_, _) => timer.Stop();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            drawing.DrawRectangle(Brushes.DarkGreen, null, new Rect(0, 0, ActualWidth, ActualHeight));
            drawing.DrawRectangle(clock.Elapsed.TotalSeconds % 2 < .2 ? Brushes.White : Brushes.Black,
                null, new Rect(ActualWidth / 4, ActualHeight / 4, ActualWidth / 2, ActualHeight / 2));
        }
    }

    private sealed class SyncTone(Stopwatch clock) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        private long sample;
        public int Read(float[] buffer, int offset, int count)
        {
            var now = clock.Elapsed.TotalSeconds;
            for (var i = 0; i < count; i += 2)
            {
                var value = (now + i / 96000d) % 2 < .2
                    ? .08f * MathF.Sin(2 * MathF.PI * 1000 * sample / 48000f) : 0;
                sample++;
                buffer[offset + i] = buffer[offset + i + 1] = value;
            }
            return count;
        }
    }
}
