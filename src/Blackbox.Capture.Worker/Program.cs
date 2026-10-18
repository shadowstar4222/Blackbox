using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Blackbox.Capture.Protocol;

namespace Blackbox.Capture.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        using var lifetime = new CancellationTokenSource();
        var output = Channel.CreateBounded<CaptureReply>(64);
        void Emit(CaptureReply reply) { if (!output.Writer.TryWrite(reply)) lifetime.Cancel(); }
        var outputTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var reply in output.Reader.ReadAllAsync())
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(reply, CaptureJson.Options));
                await Console.Out.FlushAsync();
            }
            catch (IOException) { lifetime.Cancel(); }
        });
        var host = new WorkerHost(Emit);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        using var watchdog = lifetime.Token.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(40));
                // Native driver deadlocks must not keep recording after the parent has gone.
                Environment.Exit(3);
            });
        });
        var exitCode = 0;
        try
        {
            if (args is ["--probe"])
            {
                Emit(await WorkerHost.ProbeAsync(1));
            }
            else if (args.Length > 0)
            {
                exitCode = await RecordTestAsync(args, host, Emit, lifetime.Token);
            }
            else
            {
                var commands = Channel.CreateBounded<CaptureCommand>(16);
                _ = WorkerHost.ReadCommandsAsync(commands.Writer, Emit, lifetime);
                while (!lifetime.IsCancellationRequested && !host.ShutdownRequested)
                {
                    using var nextCheck = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    if (host.IsRecording) nextCheck.CancelAfter(TimeSpan.FromMilliseconds(250));
                    try
                    {
                        if (!await commands.Reader.WaitToReadAsync(nextCheck.Token)) break;
                        if (commands.Reader.TryRead(out var command))
                            Emit(await host.HandleAsync(command, lifetime.Token));
                    }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                    {
                    }
                    await host.TickAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Emit(new CaptureReply { Id = 0, Success = false, State = "failed", Error = ex.Message });
            exitCode = 1;
        }
        finally
        {
            lifetime.Cancel();
            await host.StopAsync();
            output.Writer.TryComplete();
            try { await outputTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { exitCode = 3; }
        }
        if (!host.Status().Success) exitCode = 1;
        // C++/CLI finalizers can wait on a hung driver; the isolated worker has a hard exit boundary.
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static async Task<int> RecordTestAsync(string[] args, WorkerHost host, Action<CaptureReply> emit, CancellationToken token)
    {
        if (args.Length != 5 || args[0] != "--record-test")
            throw new ArgumentException("Usage: --probe OR --record-test <decimal-or-0x-HWND> <seconds> <absolute-output-directory> <absolute-ffmpeg-path>");
        var handle = args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(args[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(args[1], CultureInfo.InvariantCulture);
        var seconds = int.Parse(args[2], CultureInfo.InvariantCulture);
        if (seconds is < 1 or > 3600) throw new ArgumentException("Test duration must be 1..3600 seconds.");
        var start = await host.HandleAsync(new CaptureCommand
        {
            Id = 1, Operation = "start", Start = new CaptureStartRequest
            {
                WindowHandle = handle, OutputDirectory = args[3], FfmpegPath = args[4],
                FilePrefix = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + "-",
                Width = 1280, Height = 720, SegmentSeconds = Math.Min(5, seconds),
                CaptureMicrophone = false, CaptureSystemAudio = false
            }
        }, token);
        emit(start);
        if (!start.Success) return 1;
        var until = Environment.TickCount64 + seconds * 1000L;
        while (Environment.TickCount64 < until && host.Status().State == "recording")
        {
            await Task.Delay(250, token);
            await host.TickAsync();
        }
        var stop = await host.HandleAsync(new CaptureCommand { Id = 2, Operation = "stop" }, token);
        emit(stop);
        return stop.Success ? 0 : 1;
    }
}
