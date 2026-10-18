using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Blackbox.Capture.Protocol;
using Microsoft.Extensions.Logging;

namespace Blackbox.Capture;

public sealed class NativeWorkerClient(NativeCaptureOptions options, ILogger<NativeWorkerClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingCommand> _pending = new();
    private Process? _process;
    private Task? _reader;
    private Task? _errors;
    private long _nextId;
    private bool _disposed;
    private int _closing;
    private sealed record PendingCommand(Process Process, TaskCompletionSource<CaptureReply> Completion);
    public event Action<CaptureReply>? Updated;

    public async Task<CaptureReply> SendAsync(CaptureCommand command, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<CaptureReply> completion;
        long id = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _closing) != 0 && command.Operation != "shutdown")
                throw new ObjectDisposedException(nameof(NativeWorkerClient));
            if (_process is null || _process.HasExited)
            {
                // Drain the previous generation before allowing a replacement to publish its status.
                if (_reader is not null) await _reader.ConfigureAwait(false);
                if (_errors is not null) await _errors.ConfigureAwait(false);
                _process?.Dispose();
                if (!File.Exists(options.WorkerPath))
                    throw new FileNotFoundException("The bundled Blackbox recording engine is missing. Install the complete portable package.", options.WorkerPath);
                var start = new ProcessStartInfo(options.WorkerPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(options.WorkerPath)!
                };
                var process = Process.Start(start) ?? throw new IOException("The recording engine could not start.");
                _process = process;
                _reader = ReadRepliesAsync(process);
                _errors = ReadErrorsAsync(process);
            }
            id = Interlocked.Increment(ref _nextId);
            command.Id = id;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = new(_process, completion);
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { if (id != 0) _pending.TryRemove(id, out _); throw; }
        finally { _gate.Release(); }

        try
        {
            var reply = await completion.Task.WaitAsync(options.CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (!reply.Success)
                throw new InvalidOperationException(reply.Error ?? "The recording engine rejected the request.");
            return reply;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadRepliesAsync(Process process)
    {
        string? failure = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                CaptureReply? reply;
                try { reply = JsonSerializer.Deserialize<CaptureReply>(line); }
                catch (JsonException) { logger.LogDebug("Ignored non-protocol output from recording engine."); continue; }
                if (reply is null) continue;
                Publish(reply);
                if (reply.Id != 0 && _pending.TryRemove(reply.Id, out var completion))
                    completion.Completion.TrySetResult(reply);
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (!_disposed && Volatile.Read(ref _closing) == 0) failure = $"Blackbox's recording engine exited (code {process.ExitCode}). Completed segments are preserved.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            if (!_disposed && Volatile.Read(ref _closing) == 0) failure = $"The recording engine disconnected: {ex.Message}";
        }
        finally
        {
            var error = new IOException(failure ?? "The recording engine stopped.");
            foreach (var pair in _pending.ToArray())
                if (ReferenceEquals(pair.Value.Process, process) && _pending.TryRemove(pair.Key, out var completion)) completion.Completion.TrySetException(error);
            if (failure is not null) Publish(new CaptureReply { Success = false, State = "failed", Error = failure });
        }
    }

    private void Publish(CaptureReply reply)
    {
        foreach (var subscriber in Updated?.GetInvocationList() ?? [])
        {
            try { ((Action<CaptureReply>)subscriber)(reply); }
            catch (Exception ex) { logger.LogError(ex, "Recording status subscriber failed."); }
        }
    }

    private async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                logger.LogDebug("Recording engine: {Message}", line);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        try
        {
            if (_process is { HasExited: false })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await SendAsync(new CaptureCommand { Operation = "shutdown" }, timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
                { logger.LogWarning(ex, "The recording worker did not acknowledge shutdown."); }
            }
        }
        finally
        {
            _disposed = true;
            if (_process is { } process)
            {
                try
                {
                    process.StandardInput.Close();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
                if (_reader is not null) await _reader.ConfigureAwait(false);
                if (_errors is not null) await _errors.ConfigureAwait(false);
                process.Dispose();
            }
            _gate.Dispose();
        }
    }
}
