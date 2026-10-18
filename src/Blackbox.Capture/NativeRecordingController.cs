using System.Diagnostics;
using System.Runtime.InteropServices;
using Blackbox.Capture.Protocol;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Capture;

public sealed class NativeRecordingController : INativeRecorderController, IObsCaptureHealthClient, IRecordingTimelineClock, IDisposable
{
    private readonly NativeWorkerClient _worker;
    private readonly NativeCaptureOptions _options;
    private readonly IRecordingQualitySettingsProvider _quality;
    private readonly IRunningApplicationCatalog _applications;
    private readonly ILogger<NativeRecordingController> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CaptureReply _status = new();
    private GameCaptureTarget? _target;
    private string? _directory;
    private int _segmentMinutes = 2;
    private string? _microphoneId;
    private bool _microphoneMuted;
    private MicrophoneProcessingSettings _microphoneProcessing = new();
    private bool _ready;
    private bool _isStarting;
    private long _lastPreviewTick;
    private readonly SemaphoreSlim _healthGate = new(1, 1);
    private long _recordingOriginTicks;
    private int _recordingFps = 60;
    private long _lastStatusTick = long.MinValue;

    public NativeRecordingController(NativeWorkerClient worker, NativeCaptureOptions options,
        IRecordingQualitySettingsProvider quality, IRunningApplicationCatalog applications,
        ILogger<NativeRecordingController> logger)
    {
        _worker = worker;
        _options = options;
        _quality = quality;
        _applications = applications;
        _logger = logger;
        _worker.Updated += OnWorkerUpdated;
    }

    public GameCaptureTarget? Target => Volatile.Read(ref _target);
    public string Description => "Windows Graphics Capture / H.264 (hardware requested) / native audio with voice DSP";
    public NativeAudioSignal? AudioSignal
    {
        get
        {
            var status = Volatile.Read(ref _status);
            var fresh = Environment.TickCount64 - Interlocked.Read(ref _lastStatusTick) <= 1500;
            return new(status.State == "recording" && !status.Paused, status.AudioCaptureEnabled,
                fresh ? status.AudioPeakDb : null, status.MicrophoneCaptureEnabled,
                fresh && status.MicrophonePacketReceived);
        }
    }
    public event Action<string>? RecordingFailed;
    public event Action? TargetClosed;
    public DateTimeOffset? RecordingTime => IsActive && Interlocked.Read(ref _recordingOriginTicks) is var ticks && ticks > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero).AddSeconds(Math.Max(_status.DurationSeconds, _status.Frames / (double)_recordingFps))
        : null;
    public Task ShutdownAsync() => _worker.DisposeAsync().AsTask();

    public async Task ClearIdleTargetAsync(int processId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsActive && Target?.ProcessId == processId) Volatile.Write(ref _target, null);
        }
        finally { _gate.Release(); }
    }

    public async Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        if (_ready) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Built-in window recording requires Windows 10 version 2004 or newer.");
        if (!File.Exists(_options.FfmpegPath))
            throw new FileNotFoundException("The bundled Blackbox video tools are missing. Use the complete portable package.", _options.FfmpegPath);
        await _worker.SendAsync(new CaptureCommand { Operation = "probe" }, cancellationToken).ConfigureAwait(false);
        _ready = true;
    }

    public async Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        try { await LaunchAsync(cancellationToken).ConfigureAwait(false); return ObsConnectionStatus.Connected("Built-in recorder ready."); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ObsConnectionStatus.Failed(ex.GetBaseException().Message); }
    }

    public Task ApplySetupPlanAsync(ObsConnectionSettings settings, ObsSetupPlan plan, CancellationToken cancellationToken = default) => LaunchAsync(cancellationToken);

    public async Task ConfigureSegmentedRecordingAsync(string recordingDirectory, int segmentMinutes,
        GameCaptureTarget? captureTarget = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingDirectory);
        if (segmentMinutes is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(segmentMinutes));
        if (IsActive) throw new InvalidOperationException("Stop the current recording before changing its destination.");
        var selected = captureTarget ?? Target ?? throw new InvalidOperationException("Select an open application in Games before recording.");
        await ConfigureGameCaptureAsync(selected, cancellationToken).ConfigureAwait(false);
        _directory = Path.GetFullPath(recordingDirectory);
        _segmentMinutes = segmentMinutes;
    }

    public Task ConfigureAudioAsync(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        microphoneSettings.Validate();
        _microphoneProcessing = microphoneSettings;
        return Task.CompletedTask;
    }

    public Task ConfigureGameCaptureAsync(GameCaptureTarget target, CancellationToken cancellationToken = default) => RefreshGameCaptureAsync(target, cancellationToken);

    public async Task RefreshGameCaptureAsync(GameCaptureTarget target, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWindowAsync(target, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsActive && (Target?.WindowHandle != resolved.WindowHandle || Target?.ProcessId != resolved.ProcessId))
            {
                await _worker.SendAsync(new CaptureCommand
                {
                    Operation = "target", WindowHandle = resolved.WindowHandle, ProcessId = resolved.ProcessId
                }, cancellationToken).ConfigureAwait(false);
            }
            Volatile.Write(ref _target, resolved);
        }
        finally { _gate.Release(); }
    }

    private async Task<GameCaptureTarget> ResolveWindowAsync(GameCaptureTarget target, CancellationToken cancellationToken)
    {
        target.Validate();
        var handle = new IntPtr(target.WindowHandle);
        if (handle == IntPtr.Zero || !IsWindow(handle) ||
            GetWindowThreadProcessId(handle, out var owner) == 0 || owner != target.ProcessId)
        {
            var applications = await _applications.GetRunningApplicationsAsync(cancellationToken).ConfigureAwait(false);
            var application = applications.FirstOrDefault(app => app.ProcessId == target.ProcessId &&
                app.ExecutablePath.Equals(target.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                app.ObsWindowIdentifier.Equals(target.ObsWindowIdentifier, StringComparison.OrdinalIgnoreCase));
            if (application is null || application.WindowHandle == 0)
                throw new InvalidOperationException($"{target.Title}'s selected window closed. Choose its current window in Games.");
            target = target with { WindowHandle = application.WindowHandle };
            handle = new IntPtr(application.WindowHandle);
        }
        if (!IsWindowVisible(handle) || IsIconic(handle))
            throw new InvalidOperationException($"Restore {target.Title}'s window before recording. Minimized windows cannot provide live video.");
        if (GetWindowDisplayAffinity(handle, out var affinity) && affinity != 0)
            throw new InvalidOperationException($"{target.Title} prevents Windows from capturing its window.");
        if (!GetClientRect(handle, out var bounds) || bounds.Right < 64 || bounds.Bottom < 64)
            throw new InvalidOperationException($"{target.Title}'s window is too small to record. Enlarge it to at least 64 by 64 pixels.");
        target = target with { WindowWidth = bounds.Right, WindowHeight = bounds.Bottom };
        return target;
    }

    private bool IsActive => Volatile.Read(ref _status).State is "recording" or "paused" or "finishing";

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        await LaunchAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsActive) return;
            var target = await ResolveWindowAsync(Target ?? throw new InvalidOperationException("No capture window is selected."), cancellationToken).ConfigureAwait(false);
            var directory = _directory ?? throw new InvalidOperationException("A recording destination is required.");
            Directory.CreateDirectory(directory);
            var quality = _quality.Current.ForNativeRecording();
            quality.Validate();
            var (width, height) = NativeRecordingSizing.Resolve(quality.Resolution, target.WindowWidth, target.WindowHeight);
            _isStarting = true;
            var localStart = DateTime.Now;
            localStart = localStart.AddTicks(-(localStart.Ticks % TimeSpan.TicksPerSecond));
            Interlocked.Exchange(ref _recordingOriginTicks, new DateTimeOffset(localStart).UtcTicks);
            _recordingFps = quality.FramesPerSecond;
            var result = await _worker.SendAsync(new CaptureCommand
            {
                Operation = "start",
                Start = new CaptureStartRequest
                {
                    WindowHandle = target.WindowHandle, ProcessId = target.ProcessId,
                    OutputDirectory = directory,
                    FilePrefix = $"{localStart:yyyy-MM-dd HH-mm-ss}-{Guid.NewGuid():N}",
                    FfmpegPath = _options.FfmpegPath,
                    Width = width, Height = height, FramesPerSecond = quality.FramesPerSecond,
                    VideoBitrate = NativeRecordingSizing.VideoBitrate(width, height, quality.FramesPerSecond),
                    AudioBitrate = quality.AudioBitrateKbps * 1000, SegmentSeconds = _segmentMinutes * 60,
                    CaptureSystemAudio = target.CaptureGameAudio,
                    CaptureMicrophone = !_microphoneMuted && _microphoneId is not null,
                    MicrophoneId = _microphoneId == "default" ? null : _microphoneId,
                    MicrophoneGainDb = _microphoneProcessing.InputGainDb,
                    MicrophoneProcessing = ToCaptureSettings(_microphoneProcessing)
                }
            }, cancellationToken).ConfigureAwait(false);
            if (result.State != "recording" || result.Frames <= 0)
                throw new InvalidOperationException("The encoder did not confirm live video. Recording was not started.");
            Volatile.Write(ref _status, result);
            _lastPreviewTick = 0;
            _logger.LogInformation("Native capture started for {Title}, HWND {Handle}, {Width}x{Height}, {Fps} FPS. {Evidence}",
                target.Title, target.WindowHandle, width, height, quality.FramesPerSecond, result.Evidence);
        }
        catch
        {
            try { await _worker.SendAsync(new CaptureCommand { Operation = "stop" }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not finalize a failed recording start."); }
            throw;
        }
        finally { _isStarting = false; _gate.Release(); }
    }

    public async Task<string?> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready) return Volatile.Read(ref _status).OutputPath;
            var result = await _worker.SendAsync(new CaptureCommand { Operation = "stop" }, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _status, result);
            return result.OutputPath;
        }
        finally { _gate.Release(); }
    }

    public async Task<ObsRecordingStatus> GetRecordingStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_ready) return new(false, false, TimeSpan.Zero, 0);
        var status = await GetRecentStatusAsync(cancellationToken).ConfigureAwait(false);
        return new(status.State is "recording" or "paused", status.Paused, TimeSpan.FromSeconds(Math.Max(0, status.DurationSeconds)), status.BytesWritten);
    }

    public async Task ConfigureMicrophoneAsync(
        string? deviceId,
        MicrophoneProcessingSettings processingSettings,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processingSettings);
        processingSettings.Validate();
        _microphoneId = deviceId;
        _microphoneProcessing = processingSettings;
        _microphoneMuted = muted;
        if (IsActive)
            await _worker.SendAsync(new CaptureCommand
            {
                Operation = "microphone",
                MicrophoneId = deviceId == "default" ? null : deviceId,
                MicrophoneGainDb = processingSettings.InputGainDb,
                MicrophoneProcessing = ToCaptureSettings(processingSettings),
                Muted = muted
            }, cancellationToken).ConfigureAwait(false);
    }

    private static CaptureMicrophoneProcessing ToCaptureSettings(MicrophoneProcessingSettings settings) => new()
    {
        Preset = settings.Preset.ToString(),
        Enabled = settings.ProcessingEnabled,
        HighPassEnabled = settings.HighPassEnabled,
        HighPassCutoffHz = settings.HighPassCutoffHz,
        NoiseSuppressionEnabled = settings.NoiseSuppressionEnabled,
        NoiseSuppressionDb = settings.NoiseSuppressionDb,
        ExpanderEnabled = settings.ExpanderEnabled,
        ExpanderThresholdDb = settings.ExpanderThresholdDb,
        ExpanderRatio = settings.ExpanderRatio,
        LowMidEqDb = settings.LowMidEqDb,
        PresenceEqDb = settings.PresenceEqDb,
        CompressorEnabled = settings.CompressorEnabled,
        CompressorThresholdDb = settings.CompressorThresholdDb,
        CompressorRatio = settings.CompressorRatio,
        CompressorMakeupGainDb = settings.CompressorMakeupGainDb,
        DeEsserEnabled = settings.DeEsserEnabled,
        DeEsserThresholdDb = settings.DeEsserThresholdDb,
        LimiterEnabled = settings.LimiterEnabled,
        LimiterThresholdDb = settings.LimiterThresholdDb
    };

    public async Task<ObsCaptureHealthSample> CaptureAsync(GameCaptureTarget? target, bool includePreview, CancellationToken cancellationToken = default)
    {
        var status = await GetRecentStatusAsync(cancellationToken).ConfigureAwait(false);
        var selected = Target;
        var exists = selected is not null && IsWindow(new IntPtr(selected.WindowHandle)) &&
            GetWindowThreadProcessId(new IntPtr(selected.WindowHandle), out var owner) != 0 && owner == selected.ProcessId;
        ObsPreviewFrame? preview = null;
        var sampled = false;
        if (includePreview && status.State == "recording" && !status.Paused && await _healthGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (Environment.TickCount64 - _lastPreviewTick >= 12_000)
                {
                    _lastPreviewTick = Environment.TickCount64;
                    sampled = true;
                    try
                    {
                        var snapshot = await _worker.SendAsync(new CaptureCommand { Operation = "snapshot" }, cancellationToken).ConfigureAwait(false);
                        if (snapshot.SnapshotPngBase64 is { Length: <= 350000 } png)
                            preview = new(Convert.FromBase64String(png), "image/png", DateTimeOffset.UtcNow);
                    }
                    catch (InvalidOperationException ex) { _logger.LogDebug(ex, "Native preview was unavailable for this check."); }
                }
            }
            finally { _healthGate.Release(); }
        }
        return new ObsCaptureHealthSample(DateTimeOffset.UtcNow, exists,
            exists && !IsIconic(new IntPtr(selected!.WindowHandle)),
            target is null || (selected?.WindowHandle == target.WindowHandle && selected.ProcessId == target.ProcessId),
            selected?.ObsWindowIdentifier, "native_window", null, status.Width, status.Height,
            status.DurationSeconds > 0 ? Math.Min(_recordingFps, status.Frames / status.DurationSeconds) : 0,
            status.State is "recording" or "paused", status.AudioPeakDb, status.AudioPeakDb is null ? 0 : 1, preview,
            status.Error ?? status.Diagnostic ?? status.Evidence ?? Description)
        { SupportsPreview = true, SupportsAudioMeter = status.AudioCaptureEnabled, PreviewSampled = sampled,
            SupportsAutomaticCaptureRefresh = false,
            MicrophoneCaptureEnabled = status.MicrophoneCaptureEnabled,
            MicrophonePacketReceived = status.MicrophonePacketReceived,
            MicrophonePacketCount = status.MicrophonePacketCount };
    }

    private void OnWorkerUpdated(CaptureReply reply)
    {
        if (reply.Id != 0 && !reply.Success) return;
        var wasRecording = IsActive;
        Volatile.Write(ref _status, reply);
        Interlocked.Exchange(ref _lastStatusTick, Environment.TickCount64);
        if (reply.Id == 0 && reply.Success && reply.State == "stopped" &&
            reply.Diagnostic == "target_closed" && wasRecording && !_isStarting)
        {
            _logger.LogInformation("The selected window closed; native recording finalized normally.");
            TargetClosed?.Invoke();
        }
        if (!reply.Success && reply.Id == 0)
        {
            _ready = false;
            var message = reply.Error ?? "Native recording stopped unexpectedly.";
            _logger.LogError("{CaptureError}", message);
            if (wasRecording && !_isStarting) RecordingFailed?.Invoke(message);
        }
    }

    private Task<CaptureReply> GetRecentStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var refreshedAt = Interlocked.Read(ref _lastStatusTick);
        return !_ready || refreshedAt != long.MinValue && Environment.TickCount64 - refreshedAt < 1500
            ? Task.FromResult(Volatile.Read(ref _status))
            : _worker.SendAsync(new CaptureCommand { Operation = "status" }, cancellationToken);
    }

    public void Dispose() { _worker.Updated -= OnWorkerUpdated; _gate.Dispose(); _healthGate.Dispose(); }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}

public static class NativeRecordingSizing
{
    public static (int Width, int Height) Resolve(RecordingResolution resolution, int windowWidth, int windowHeight)
    {
        if (!Enum.IsDefined(resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        if (windowWidth < 64 || windowHeight < 64) throw new ArgumentOutOfRangeException(nameof(windowWidth), "Capture windows must be at least 64 by 64 pixels.");
        var maximum = resolution switch
        {
            RecordingResolution.Hd720 => (1280, 720),
            RecordingResolution.FullHd1080 => (1920, 1080),
            RecordingResolution.QuadHd1440 => (2560, 1440),
            RecordingResolution.UltraHd2160 => (3840, 2160),
            _ => (Math.Clamp(windowWidth, 2, 3840), Math.Clamp(windowHeight, 2, 2160))
        };
        // Preserve portrait/ultrawide aspect ratio and avoid wasting encoder work upscaling small windows.
        var ratio = Math.Min(1, Math.Min(maximum.Item1 / (double)Math.Max(2, windowWidth), maximum.Item2 / (double)Math.Max(2, windowHeight)));
        var width = (int)(windowWidth * ratio) / 2 * 2;
        var height = (int)(windowHeight * ratio) / 2 * 2;
        if (width < 64 || height < 64) throw new InvalidOperationException("This window's aspect ratio is too extreme for the selected recording resolution.");
        return (width, height);
    }

    public static int VideoBitrate(int width, int height, int fps) =>
        (int)Math.Clamp((long)width * height * Math.Min(fps, 120) / 10, 2_000_000, 50_000_000);
}
