using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Blackbox.Capture.Protocol;

namespace Blackbox.Capture.Worker;

internal sealed class FfmpegMuxer : IDisposable
{
    private readonly Process process;
    private readonly ChildProcessJob job;
    private readonly Task progressReader;
    private readonly Task errorReader;
    private readonly StringBuilder errorTail = new();
    private long muxedFrames;
    private long durationMicroseconds;

    public EncodedPipeStream Input { get; }
    public NamedPipeServerStream? MicrophoneInput { get; }
    public long MuxedFrames => Interlocked.Read(ref muxedFrames);
    public double DurationSeconds => Math.Max(0, Interlocked.Read(ref durationMicroseconds) / 1_000_000.0);
    public string ErrorTail { get { lock (errorTail) return errorTail.ToString().Trim(); } }
    public string? Failure => Input.Failure ?? (process.HasExited ? $"FFmpeg exited ({process.ExitCode}): {ErrorTail}" : null);

    public FfmpegMuxer(CaptureStartRequest request, string pattern, string csvPath)
    {
        var info = new ProcessStartInfo(request.FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = request.OutputDirectory
        };
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin", "-nostats", "-n",
            "-thread_queue_size", "512", "-probesize", "1048576", "-analyzeduration", "1000000",
            "-i", "pipe:0"
        };
        if (request.CaptureMicrophone)
        {
            var pipeName = $"blackbox-mic-{Environment.ProcessId}-{Guid.NewGuid():N}";
            MicrophoneInput = new NamedPipeServerStream(
                pipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                262_144,
                262_144);
            arguments.AddRange([
                "-thread_queue_size", "256", "-f", "f32le", "-ar", "48000", "-ac", "1",
                "-i", $"\\\\.\\pipe\\{pipeName}",
                "-map", "0:v:0"
            ]);
            if (request.CaptureSystemAudio)
            {
                arguments.AddRange([
                    "-filter_complex",
                    "[0:a:0]aresample=48000:async=1000:first_pts=0[game];" +
                    "[1:a:0]aresample=48000:async=1000:first_pts=0[mic];" +
                    "[game][mic]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0," +
                    "alimiter=limit=0.95[audio]",
                    "-map", "[audio]"
                ]);
            }
            else
            {
                arguments.AddRange([
                    "-filter_complex", "[1:a:0]aresample=48000:async=1000:first_pts=0,pan=stereo|c0=c0|c1=c0[audio]",
                    "-map", "[audio]"
                ]);
            }

            arguments.AddRange([
                "-c:v", "copy", "-c:a", "aac", "-b:a", request.AudioBitrate.ToString(CultureInfo.InvariantCulture),
                "-ar", "48000", "-ac", "2"
            ]);
        }
        else
        {
            arguments.AddRange(["-map", "0", "-c", "copy"]);
        }

        arguments.AddRange([
            "-metadata:s:v:0", "BLACKBOX_NOMINAL_FPS=" + request.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-f", "segment",
            "-segment_time", request.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-reset_timestamps", "1", "-segment_format", "matroska",
            "-segment_list", csvPath, "-segment_list_type", "csv", "-segment_list_size", "0",
            "-segment_format_options", "cluster_time_limit=1000",
            "-flush_packets", "1", "-progress", "pipe:1", "-stats_period", "0.25", pattern
        ]);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        process = Process.Start(info) ?? throw new IOException("FFmpeg could not be started.");
        try { job = new ChildProcessJob(process); }
        catch { Kill(); process.Dispose(); throw; }
        Input = new EncodedPipeStream(process.StandardInput.BaseStream);
        progressReader = ReadProgressAsync();
        errorReader = ReadErrorsAsync();
    }

    private async Task ReadProgressAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                var separator = line.IndexOf('=');
                if (separator < 0 || !long.TryParse(line.AsSpan(separator + 1), out var value)) continue;
                if (line.StartsWith("frame=", StringComparison.Ordinal)) Interlocked.Exchange(ref muxedFrames, value);
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) Interlocked.Exchange(ref durationMicroseconds, value);
            }
        }
        catch (IOException) { }
    }

    private async Task ReadErrorsAsync()
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer)) > 0)
            {
                lock (errorTail)
                {
                    errorTail.Append(buffer, 0, count);
                    if (errorTail.Length > 8192) errorTail.Remove(0, errorTail.Length - 8192);
                }
            }
        }
        catch (IOException) { }
    }

    public async Task<string?> FinishAsync()
    {
        await Input.CompleteAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            Kill();
            return "FFmpeg finalization exceeded 8 seconds; process killed. Last segment may be incomplete. " + ErrorTail;
        }
        await Task.WhenAll(progressReader, errorReader).WaitAsync(TimeSpan.FromSeconds(2));
        if (Input.Failure is { } error) return error + " " + ErrorTail;
        return process.ExitCode == 0 ? null : $"FFmpeg exited ({process.ExitCode}): {ErrorTail}";
    }

    public void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Input.Abort();
        MicrophoneInput?.Dispose();
        Kill();
        job.Dispose();
        process.Dispose();
    }
}
