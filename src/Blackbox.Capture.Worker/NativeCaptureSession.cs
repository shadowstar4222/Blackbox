using System.Diagnostics;
using Blackbox.Domain;
using Blackbox.Capture.Protocol;
using ScreenRecorderLib;

namespace Blackbox.Capture.Worker;

internal sealed class NativeCaptureSession
{
    private readonly CaptureStartRequest request;
    private readonly AudioPeakMeter audioMeter = new();
    private readonly TaskCompletionSource nativeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Recorder? recorder;
    private WindowRecordingSource? source;
    private ProcessedMicrophoneCapture? microphone;
    private ProcessAudioSource? processAudio;
    private FfmpegMuxer? muxer;
    private SegmentFinalizer? segments;
    private FileStream? reservation;
    private WindowTarget target;
    private string? nativeError;
    private long frames;
    private long lastFrameTick = Environment.TickCount64;
    private long lastOutputTick = Environment.TickCount64;
    private long lastPipeBytes;
    private long bytesWritten;
    private double duration;
    private string? outputPath;
    private string audioEvidence = "audio=disabled";
    private bool stopCalled;
    private bool targetLost;
    private long targetLostAt;
    private long lastSnapshotTick = long.MinValue;
    public string State { get; private set; } = "starting";
    public string? Error { get; private set; }
    public string? Diagnostic { get; private set; }
    public bool Paused { get; private set; }
    public bool MustExit { get; private set; }

    public NativeCaptureSession(CaptureStartRequest request)
    {
        ValidateRequest(request);
        request.FilePrefix = request.FilePrefix.TrimEnd('-');
        if (string.IsNullOrWhiteSpace(request.FilePrefix)) throw new ArgumentException("FilePrefix must contain more than hyphens/spaces.");
        this.request = request;
        target = WindowTarget.Validate(request.WindowHandle, request.ProcessId, requireAvailable: true);
    }

    public static void ValidateRequest(CaptureStartRequest request)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Window capture requires Windows 10 build 18362+ and an x64 worker.");
        if (request.Width is < 64 or > 3840 || request.Height is < 64 or > 2160 ||
            (request.Width & 1) != 0 || (request.Height & 1) != 0)
            throw new ArgumentException("Width/Height must be even, 64..3840 by 64..2160 (fixed output canvas).");
        if (request.FramesPerSecond is < 1 or > 120 || request.VideoBitrate is < 100_000 or > 100_000_000)
            throw new ArgumentException("FPS must be 1..120; video bitrate must be 100000..100000000 bits/s.");
        if (request.AudioBitrate is not (96_000 or 128_000 or 160_000 or 192_000))
            throw new ArgumentException("AudioBitrate must be 96000, 128000, 160000, or 192000 bits/s.");
        if (request.SegmentSeconds is < 1 or > 3600)
            throw new ArgumentException("SegmentSeconds must be 1..3600.");
        if (!double.IsFinite(request.MicrophoneGainDb) || request.MicrophoneGainDb is < -30 or > 30)
            throw new ArgumentException("MicrophoneGainDb must be finite and between -30 and +30 dB.");
        if (request.CaptureMicrophone)
            ToProcessingSettings(request.MicrophoneProcessing, request.MicrophoneGainDb).Validate();
        if (string.IsNullOrWhiteSpace(request.FilePrefix) || request.FilePrefix.Length > 100 ||
            request.FilePrefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ' ')))
            throw new ArgumentException("FilePrefix must be 1..100 ASCII letters, digits, spaces, dots, hyphens, or underscores.");
        if (!Path.IsPathFullyQualified(request.OutputDirectory) || request.OutputDirectory.Contains('%'))
            throw new ArgumentException("OutputDirectory must be absolute and cannot contain '%'.");
        if (!Path.IsPathFullyQualified(request.FfmpegPath) || !File.Exists(request.FfmpegPath))
            throw new FileNotFoundException("FfmpegPath must point to the bundled ffmpeg.exe using an absolute path.");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        var lockPath = Path.Combine(request.OutputDirectory, request.FilePrefix + ".capture.lock");
        reservation = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose);
        if (Directory.EnumerateFiles(request.OutputDirectory, request.FilePrefix + "-*.mkv").Any())
            throw new IOException("Output prefix already has MKV files. Use a new prefix; existing recordings are never overwritten.");
        segments = new SegmentFinalizer(request.OutputDirectory, request.FilePrefix);
        if (File.Exists(segments.CsvPath)) throw new IOException("Output prefix already has a segment CSV; use a new prefix.");
        var options = BuildOptions();
        var pattern = Path.Combine(request.OutputDirectory, request.FilePrefix + "-%04d.active.mkv");
        outputPath = Path.Combine(request.OutputDirectory, request.FilePrefix + "-0000.active.mkv");
        muxer = new FfmpegMuxer(request, pattern, segments.CsvPath);
        if (request.CaptureMicrophone)
        {
            microphone = new ProcessedMicrophoneCapture(
                muxer.MicrophoneInput ?? throw new IOException("Microphone audio pipe was not created."),
                request.MicrophoneId,
                ToProcessingSettings(request.MicrophoneProcessing, request.MicrophoneGainDb));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await NativeCallAsync(() =>
        {
            recorder = Recorder.CreateRecorder(options);
            recorder.OnFrameRecorded += (_, _) =>
            {
                Interlocked.Increment(ref frames);
                Interlocked.Exchange(ref lastFrameTick, Environment.TickCount64);
            };
            recorder.OnAudioPacketRecorded += (_, e) => audioMeter.Add(e.AudioData.Gain, Environment.TickCount64);
            recorder.OnRecordingFailed += (_, e) =>
            {
                Interlocked.CompareExchange(ref nativeError, "Native capture failed: " + e.Error, null);
                nativeDone.TrySetResult();
            };
            recorder.OnRecordingComplete += (_, _) => nativeDone.TrySetResult();
            recorder.Record(muxer.Input);
        });
        microphone?.Start();

        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (target.GetDiagnostic() is { } diagnostic) throw new IOException(diagnostic);
                ThrowIfFailed();
                RefreshOutput();
                if (Interlocked.Read(ref frames) >= 2 && muxer.MuxedFrames > 0 && bytesWritten > 0 &&
                    (!request.CaptureMicrophone || microphone!.PacketCount > 0))
                    break;
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No verified encoded output within 20 seconds. Native frames={frames}, " +
                $"pipe bytes={muxer.Input.BytesWritten}, muxed frames={muxer.MuxedFrames}. {muxer.ErrorTail}");
        }
        State = "recording";
        lastOutputTick = Environment.TickCount64;
    }

    private RecorderOptions BuildOptions()
    {
        source = new WindowRecordingSource((nint)target.Handle)
        {
            OutputSize = new ScreenSize(request.Width, request.Height),
            Stretch = StretchMode.Uniform,
            IsVideoCaptureEnabled = true,
            IsVideoFramePreviewEnabled = false,
            IsCursorCaptureEnabled = true,
            IsBorderRequired = true
        };
        var audioSources = new List<AudioSourceBase>();
        if (request.CaptureSystemAudio)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                processAudio = new ProcessAudioSource(target.ProcessId);
                audioSources.Add(processAudio);
                audioEvidence = "audio=process-tree (Windows build >=20348); no silent system-mix retry";
            }
            else
            {
                audioSources.Add(LoopbackAudioSource.Default ?? throw new IOException("No default audio output device for system-mix fallback."));
                audioEvidence = $"audio=system-mix fallback; OS build {Environment.OSVersion.Version.Build} <20348 lacks process loopback";
            }
        }
        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { source } },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(request.Width, request.Height),
                Stretch = StretchMode.Uniform,
                IsVideoCaptureEnabled = true,
                IsVideoFramePreviewEnabled = false
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { EncoderProfile = H264Profile.High, BitrateMode = H264BitrateControlMode.CBR },
                Framerate = request.FramesPerSecond,
                Bitrate = request.VideoBitrate,
                // Keep capture-clock timestamps when frames arrive late under load.
                IsFixedFramerate = false,
                IsHardwareEncodingEnabled = true,
                IsLowLatencyEnabled = true,
                IsThrottlingDisabled = false,
                IsFragmentedMp4Enabled = true,
                IsMp4FastStartEnabled = false
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioSources.Count > 0,
                AudioSources = audioSources,
                Bitrate = (AudioBitrate)(request.AudioBitrate / 8),
                Channels = AudioChannels.Stereo,
                IsAudioPacketPreviewEnabled = false
            },
            SnapshotOptions = new SnapshotOptions { SnapshotsWithVideo = false },
            LogOptions = new LogOptions
            {
                IsLogEnabled = Environment.GetEnvironmentVariable("BLACKBOX_NATIVE_DEBUG") == "1",
                LogFilePath = Path.Combine(request.OutputDirectory, request.FilePrefix + ".native.log"),
                LogSeverityLevel = LogLevel.Debug
            }
        };
    }

    public async Task<bool> TickAsync()
    {
        if (State != "recording") return false;
        ThrowIfFailed();
        segments?.Poll();
        var diagnostic = target.GetDiagnostic();
        if (diagnostic?.StartsWith("target_closed", StringComparison.Ordinal) == true ||
            diagnostic?.StartsWith("target_reused", StringComparison.Ordinal) == true)
        {
            if (!targetLost) targetLostAt = Environment.TickCount64;
            targetLost = true;
        }
        if (targetLost && Environment.TickCount64 - targetLostAt >= 5000)
        {
            Diagnostic = "target_closed";
            await StopAsync();
            return true;
        }
        diagnostic = targetLost ? "target_closed: waiting for an explicit target command" : diagnostic;
        var changed = diagnostic != Diagnostic;
        if (diagnostic is not null && !Paused)
        {
            await NativeCallAsync(() => recorder!.Pause());
            microphone?.SetPaused(true);
            Paused = true;
        }
        else if (diagnostic is null && Paused)
        {
            await NativeCallAsync(() => recorder!.Resume());
            microphone?.SetPaused(false);
            Paused = false;
            lastFrameTick = lastOutputTick = Environment.TickCount64;
        }
        Diagnostic = diagnostic;
        if (!Paused)
        {
            if (muxer!.Input.BytesWritten != lastPipeBytes)
            {
                lastPipeBytes = muxer.Input.BytesWritten;
                lastOutputTick = Environment.TickCount64;
            }
            if (Environment.TickCount64 - Interlocked.Read(ref lastFrameTick) > 15_000)
                throw new IOException("Native video frame progress stalled for 15 seconds.");
            if (Environment.TickCount64 - lastOutputTick > 20_000)
                throw new IOException("Encoded output stalled for 20 seconds. " + muxer.ErrorTail);
        }
        return changed;
    }

    public async Task ChangeTargetAsync(long handle, int pid, CancellationToken cancellationToken)
    {
        var next = WindowTarget.Validate(handle, pid, requireAvailable: true);
        var nextSource = new WindowRecordingSource(source!) { Handle = (nint)next.Handle };
        var nextAudio = processAudio is null ? null : new ProcessAudioSource(processAudio) { ProcessId = next.ProcessId };
        await NativeCallAsync(() =>
        {
            // DynamicOptionsBuilder requires Recording, even when changing a paused source.
            if (Paused) recorder!.Resume();
            var builder = recorder!.GetDynamicOptionsBuilder().SetUpdatedRecordingSource(nextSource);
            if (nextAudio is not null) builder.SetUpdatedAudioSource(nextAudio);
            if (!builder.Apply()) throw new IOException("Native source change was not accepted.");
        });
        source = nextSource;
        processAudio = nextAudio;
        target = next;
        Paused = targetLost = false;
        microphone?.SetPaused(false);
        Diagnostic = null;
        lastFrameTick = lastOutputTick = Environment.TickCount64;
        var initialFrames = Interlocked.Read(ref frames);
        var initialBytes = muxer!.Input.BytesWritten;
        var deadline = Stopwatch.StartNew();
        while (Interlocked.Read(ref frames) <= initialFrames || muxer.Input.BytesWritten <= initialBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailed();
            if (next.GetDiagnostic() is { } diagnostic) throw new IOException(diagnostic);
            if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("No native frame/output progress after target change.");
            await Task.Delay(100, cancellationToken);
        }
    }

    public Task ChangeMicrophoneAsync(
        string? id,
        bool muted,
        double? gainDb,
        CaptureMicrophoneProcessing? processing)
    {
        if (microphone is null) throw new InvalidOperationException("Microphone capture was disabled at start; enabling it requires a new session.");
        if (gainDb is { } gain && (!double.IsFinite(gain) || gain is < -30 or > 30))
            throw new ArgumentException("MicrophoneGainDb must be finite and between -30 and +30 dB.");
        if (!string.IsNullOrWhiteSpace(id) && !id.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !id.Equals(microphone.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Changing microphone devices during a recording requires starting a new session.");
        var current = microphone.Settings;
        var next = processing is null
            ? current with { InputGainDb = gainDb ?? current.InputGainDb }
            : ToProcessingSettings(processing, gainDb ?? current.InputGainDb);
        microphone.Update(next, muted);
        return Task.CompletedTask;
    }

    public async Task<CaptureReply> SnapshotAsync(long id)
    {
        if (Paused || target.GetDiagnostic() is not null) throw new InvalidOperationException("Snapshot is unavailable while the target is paused or unavailable.");
        var now = Environment.TickCount64;
        if (lastSnapshotTick != long.MinValue && now - lastSnapshotTick < 10_000)
            throw new InvalidOperationException("Snapshots are rate limited to one request every 10 seconds.");
        lastSnapshotTick = now;
        using var png = new BoundedSnapshotStream();
        await NativeCallAsync(() =>
        {
            if (!recorder!.TakeSnapshot(png)) throw new IOException("Native current-frame snapshot failed.");
        });
        if (png.Overflowed || png.Length == 0) throw new IOException("Native snapshot is empty or exceeded 40 MiB.");
        var reply = Reply(id);
        CaptureSnapshot.FillReply(png, reply);
        return reply;
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref nativeError) is { } error) throw new IOException(error);
        if (muxer?.Failure is { } muxerError) throw new IOException(muxerError);
        if (microphone?.Failure is { } microphoneError) throw new IOException(microphoneError);
        if (nativeDone.Task.IsCompleted && !stopCalled) throw new IOException("Native recording ended unexpectedly.");
    }

    private void RefreshOutput()
    {
        if (muxer is not null) duration = muxer.DurationSeconds;
        if (reservation is null) return;
        // Closed bytes are accumulated once from the CSV. Only stat the current segment,
        // so status polling stays constant-cost throughout multi-hour recordings.
        long bytes = segments?.FinalizedBytes ?? 0;
        var active = Path.Combine(request.OutputDirectory,
            $"{request.FilePrefix}-{segments?.NextIndex ?? 0:D4}.active.mkv");
        var file = new FileInfo(active);
        if (file.Exists)
        {
            bytes += file.Length;
            outputPath = active;
        }
        else if (segments?.LastFinalizedPath is { } finalized) outputPath = finalized;
        bytesWritten = Math.Max(bytesWritten, bytes);
    }

    public CaptureReply Reply(long id = 0)
    {
        RefreshOutput();
        var audio = audioMeter.Read(Environment.TickCount64);
        return new CaptureReply
        {
            Id = id, Success = Error is null, Error = Error, State = State,
            Frames = Interlocked.Read(ref frames), BytesWritten = bytesWritten, DurationSeconds = duration,
            WindowHandle = target.Handle, ProcessId = target.ProcessId,
            Width = request.Width, Height = request.Height, OutputPath = outputPath,
            Paused = Paused, Diagnostic = Diagnostic,
            AudioCaptureEnabled = request.CaptureSystemAudio,
            AudioPeakDb = State == "recording" && !Paused ? audio.PeakDb : null,
            AudioPacketCount = audio.Packets,
            MicrophoneCaptureEnabled = microphone?.IsEnabled == true,
            MicrophonePacketReceived = (microphone?.PacketCount ?? 0) > 0,
            MicrophonePacketCount = microphone?.PacketCount ?? 0,
            MicrophoneProcessingEnabled = microphone?.ProcessingEnabled == true,
            MicrophonePreset = microphone?.Preset,
            MicrophoneDroppedFrameCount = microphone?.DroppedFrames ?? 0,
            Evidence = "ScreenRecorderLib 7.0.1; WGC exact HWND; GPU fixed-canvas scaling; H264 hardware requested " +
                "(library permits fallback and does not expose selected encoder); low-latency; throttling enabled; " +
                "previews disabled; bounded fragmented MP4 stream -> FFmpeg stream-copy MKV; " + audioEvidence +
                $"; microphone={(microphone is null ? "disabled" : microphone.IsEnabled ? "enabled" : "muted")}; " +
                $"microphoneProcessing={(microphone?.ProcessingEnabled == true ? microphone.Preset : "off")}; " +
                $"microphonePackets={microphone?.PacketCount ?? 0}; microphoneDropped={microphone?.DroppedFrames ?? 0}; " +
                $"nativeFrames={frames}; pipeBytes={muxer?.Input.BytesWritten ?? lastPipeBytes}; muxedFrames={muxer?.MuxedFrames ?? 0}"
        };
    }

    public async Task StopAsync(string? failure = null)
    {
        if (stopCalled) return;
        stopCalled = true;
        Error = failure ?? Volatile.Read(ref nativeError);
        try
        {
            if (recorder is not null && !MustExit)
            {
                muxer?.Input.BeginFinalization();
                await NativeCallAsync(recorder.Stop);
                if (!nativeDone.Task.IsCompleted)
                {
                    try { await nativeDone.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
                    catch (TimeoutException) { MustExit = true; throw new TimeoutException("Native finalization exceeded 8 seconds; last segment may be incomplete."); }
                }
                await NativeCallAsync(recorder.Dispose);
                recorder = null;
            }
        }
        catch (Exception ex) { Error = JoinError(Error, ex.Message); }
        if (microphone is not null)
        {
            try { await microphone.DisposeAsync(); }
            catch (Exception ex) { Error = JoinError(Error, ex.Message); }
            finally { microphone = null; }
        }
        if (muxer is not null)
        {
            try
            {
                if (MustExit) muxer.Kill();
                var muxerError = await muxer.FinishAsync();
                Error = JoinError(Error, muxerError);
                segments?.Poll();
                RefreshOutput();
                lastPipeBytes = muxer.Input.BytesWritten;
            }
            catch (Exception ex) { Error = JoinError(Error, ex.Message); }
            finally { muxer.Dispose(); muxer = null; }
        }
        Error = JoinError(Error, Volatile.Read(ref nativeError));
        reservation?.Dispose();
        reservation = null;
        State = Error is null ? "stopped" : "failed";
        Paused = false;
    }

    private async Task NativeCallAsync(Action action)
    {
        try { await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            MustExit = true;
            throw new TimeoutException("Native API call exceeded 5 seconds; this worker must be restarted.");
        }
    }

    private static string? JoinError(string? first, string? second) =>
        string.IsNullOrEmpty(second) || first?.Contains(second, StringComparison.Ordinal) == true ? first :
        string.IsNullOrEmpty(first) ? second : first + " | " + second;

    private static MicrophoneProcessingSettings ToProcessingSettings(
        CaptureMicrophoneProcessing processing,
        double inputGainDb)
    {
        if (!Enum.TryParse<MicrophoneVoicePreset>(processing.Preset, true, out var preset) || !Enum.IsDefined(preset))
            throw new ArgumentException("Microphone preset is invalid.");
        return new MicrophoneProcessingSettings
        {
            Preset = preset,
            ProcessingEnabled = processing.Enabled,
            InputGainDb = inputGainDb,
            HighPassEnabled = processing.HighPassEnabled,
            HighPassCutoffHz = processing.HighPassCutoffHz,
            NoiseSuppressionEnabled = processing.NoiseSuppressionEnabled,
            NoiseSuppressionDb = processing.NoiseSuppressionDb,
            ExpanderEnabled = processing.ExpanderEnabled,
            ExpanderThresholdDb = processing.ExpanderThresholdDb,
            ExpanderRatio = processing.ExpanderRatio,
            LowMidEqDb = processing.LowMidEqDb,
            PresenceEqDb = processing.PresenceEqDb,
            CompressorEnabled = processing.CompressorEnabled,
            CompressorThresholdDb = processing.CompressorThresholdDb,
            CompressorRatio = processing.CompressorRatio,
            CompressorMakeupGainDb = processing.CompressorMakeupGainDb,
            DeEsserEnabled = processing.DeEsserEnabled,
            DeEsserThresholdDb = processing.DeEsserThresholdDb,
            LimiterEnabled = processing.LimiterEnabled,
            LimiterThresholdDb = processing.LimiterThresholdDb
        };
    }
}
