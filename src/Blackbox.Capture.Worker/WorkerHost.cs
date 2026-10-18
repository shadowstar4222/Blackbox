using System.Text.Json;
using System.Threading.Channels;
using Blackbox.Capture.Protocol;
using ScreenRecorderLib;

namespace Blackbox.Capture.Worker;

internal sealed class WorkerHost(Action<CaptureReply> emit)
{
    private NativeCaptureSession? session;
    private long lastEventTick;
    public bool ShutdownRequested { get; private set; }
    public bool IsRecording => session?.State == "recording";

    public async Task<CaptureReply> HandleAsync(CaptureCommand command, CancellationToken token)
    {
        try
        {
            if (command.Id <= 0) throw new ArgumentException("Command Id must be positive; zero is reserved for worker events.");
            switch (command.Operation?.ToLowerInvariant())
            {
                case "start":
                    if (session?.State is "recording" or "starting") throw new InvalidOperationException("A recording is already active.");
                    if (session?.MustExit == true) throw new InvalidOperationException("Native worker requires restart.");
                    var next = new NativeCaptureSession(command.Start ?? throw new ArgumentException("start requires Start."));
                    session = next;
                    try { await next.StartAsync(token); }
                    catch (Exception ex)
                    {
                        await next.StopAsync(ex is OperationCanceledException ? "Parent input closed or shutdown requested during start." : ex.Message);
                        emit(next.Reply());
                        throw;
                    }
                    emit(next.Reply());
                    break;
                case "stop":
                    await StopAsync();
                    break;
                case "status":
                    await TickAsync();
                    break;
                case "target":
                    RequireRecording();
                    try { await session!.ChangeTargetAsync(command.WindowHandle, command.ProcessId, token); }
                    catch (ArgumentException) { throw; }
                    catch (Exception ex) { await session!.StopAsync(ex.Message); emit(session.Reply()); throw; }
                    emit(session.Reply());
                    break;
                case "microphone":
                    RequireRecording();
                    await session!.ChangeMicrophoneAsync(
                        command.MicrophoneId,
                        command.Muted,
                        command.MicrophoneGainDb,
                        command.MicrophoneProcessing);
                    break;
                case "snapshot":
                    RequireRecording();
                    return await session!.SnapshotAsync(command.Id);
                case "probe":
                    return await ProbeAsync(command.Id);
                case "shutdown":
                    ShutdownRequested = true;
                    await StopAsync();
                    break;
                default:
                    throw new ArgumentException("Unknown operation. Supported: start, stop, status, target, microphone, snapshot, probe, shutdown.");
            }
            var status = Status(command.Id);
            if (command.IncludeRuntimeStatistics)
            {
                var memory = GC.GetGCMemoryInfo();
                status.RuntimeStatistics = new CaptureRuntimeStatistics(GC.GetTotalMemory(false), memory.TotalCommittedBytes,
                    memory.HeapSizeBytes, memory.FragmentedBytes, GC.GetTotalAllocatedBytes(),
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            }
            return status;
        }
        catch (Exception ex)
        {
            var reply = Status(command.Id);
            reply.Success = false;
            reply.Error = ex is OperationCanceledException ? "Worker shutdown requested." : ex.Message;
            return reply;
        }
        finally
        {
            if (session?.MustExit == true) ShutdownRequested = true;
        }
    }

    private void RequireRecording()
    {
        if (session?.State != "recording") throw new InvalidOperationException("No recording is active.");
    }

    public CaptureReply Status(long id = 0) => session?.Reply(id) ?? new CaptureReply { Id = id };

    public static async Task<CaptureReply> ProbeAsync(long id)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Requires Windows 10 build 18362+ x64.");
        // Loading the C++/CLI assembly and enumerating endpoints exercises native prerequisites.
        var evidence = await Task.Run(() =>
            $"ScreenRecorderLib {typeof(Recorder).Assembly.GetName().Version} loaded; " +
            $"captureEndpoints={Recorder.GetSystemAudioCaptureDevices().Count}; " +
            $"loopbackEndpoints={Recorder.GetSystemAudioLoopbackDevices().Count}; Windows={Environment.OSVersion.Version}; " +
            (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ? "process audio supported" : "system-mix fallback required") +
            "; WGC HWND capture; hardware encoder requested, availability verified only by start; no desktop fallback")
            .WaitAsync(TimeSpan.FromSeconds(5));
        return new CaptureReply { Id = id, Evidence = evidence };
    }

    public async Task TickAsync()
    {
        if (session?.State != "recording") return;
        try
        {
            var changed = await session.TickAsync();
            if (changed || Environment.TickCount64 - lastEventTick >= 1000)
            {
                emit(session.Reply());
                lastEventTick = Environment.TickCount64;
            }
        }
        catch (Exception ex)
        {
            await session.StopAsync(ex.Message);
            emit(session.Reply());
            if (session.MustExit) ShutdownRequested = true;
        }
    }

    public async Task StopAsync()
    {
        if (session is null || session.State is "stopped" or "failed") return;
        await session.StopAsync();
        emit(session.Reply());
    }

    public static async Task ReadCommandsAsync(ChannelWriter<CaptureCommand> writer, Action<CaptureReply> emit,
        CancellationTokenSource lifetime)
    {
        var buffer = new char[4096];
        var bytes = new byte[4096];
        var decoder = new System.Text.UTF8Encoding(false, true).GetDecoder();
        using var input = Console.OpenStandardInput();
        var line = new System.Text.StringBuilder();
        try
        {
            int count;
            while ((count = await input.ReadAsync(bytes.AsMemory(), lifetime.Token)) > 0)
            {
                count = decoder.GetChars(bytes, 0, count, buffer, 0, flush: false);
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        try
                        {
                            var command = JsonSerializer.Deserialize<CaptureCommand>(line.ToString(), CaptureJson.Options)
                                ?? throw new JsonException("Command cannot be null.");
                            if (!writer.TryWrite(command)) emit(new CaptureReply { Id = command.Id, Success = false, Error = "Command queue is full (16 pending commands)." });
                        }
                        catch (JsonException ex) { emit(new CaptureReply { Id = -1, Success = false, Error = "Malformed command: " + ex.Message }); }
                        line.Clear();
                    }
                    else if (buffer[i] != '\r') line.Append(buffer[i]);
                    if (line.Length > CaptureJson.MaximumCommandLength)
                        throw new IOException("Command exceeds the 65536-character protocol limit.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { emit(new CaptureReply { Id = -1, Success = false, Error = ex.Message }); }
        finally
        {
            writer.TryComplete();
            // Independent stdin reader cancels start/target waits immediately on parent death.
            lifetime.Cancel();
        }
    }
}
