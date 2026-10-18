using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class RecordingGuardianService : IAsyncDisposable
{
    private const int HistoryLimit = 200;
    private readonly IObsCaptureHealthClient _healthClient;
    private readonly IRecordingFrameAnalyzer _frameAnalyzer;
    private readonly IRecordingGuardianContext _context;
    private readonly IRecordingGuardianRepairer _repairer;
    private readonly RecordingGuardianOptions _options;
    private readonly RecordingGuardianEvaluator _evaluator;
    private readonly IClock _clock;
    private readonly ILogger<RecordingGuardianService> _logger;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private readonly object _historySync = new();
    private readonly List<RecordingGuardianEvent> _history = [];
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private RecordingGuardianSnapshot _snapshot;
    private string? _repairKey;
    private int _repairAttempts;
    private DateTimeOffset? _lastRepairAt;
    private int _isDisposed;

    public RecordingGuardianService(
        IObsCaptureHealthClient healthClient,
        IRecordingFrameAnalyzer frameAnalyzer,
        IRecordingGuardianContext context,
        IRecordingGuardianRepairer repairer,
        RecordingGuardianOptions options,
        IClock clock,
        ILogger<RecordingGuardianService> logger)
    {
        _healthClient = healthClient;
        _frameAnalyzer = frameAnalyzer;
        _context = context;
        _repairer = repairer;
        _options = options;
        _evaluator = new RecordingGuardianEvaluator(options);
        _clock = clock;
        _logger = logger;
        _snapshot = RecordingGuardianSnapshot.Stopped(clock.UtcNow);
    }

    public event Action<RecordingGuardianSnapshot>? SnapshotChanged;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleSync)
            {
                return _loopTask is not null;
            }
        }
    }

    public RecordingGuardianSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public IReadOnlyList<RecordingGuardianEvent> History
    {
        get
        {
            lock (_historySync)
            {
                return _history.ToArray();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        _options.Validate();
        lock (_lifecycleSync)
        {
            if (_loopTask is not null)
            {
                return Task.CompletedTask;
            }

            _loopCancellation = new CancellationTokenSource();
            _loopTask = RunAsync(_loopCancellation.Token);
        }

        AddHistory(
            DiagnosticSeverity.Information,
            RecordingGuardianIssue.None,
            "Recording Guardian started.",
            null,
            null,
            null);
        return Task.CompletedTask;
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        await CheckCoreAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loopTask;
        CancellationTokenSource? cancellation;
        lock (_lifecycleSync)
        {
            loopTask = _loopTask;
            cancellation = _loopCancellation;
            _loopTask = null;
            _loopCancellation = null;
        }

        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync();
        if (loopTask is not null)
        {
            try
            {
                await loopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        cancellation.Dispose();
        _evaluator.Reset();
        Publish(RecordingGuardianSnapshot.Stopped(_clock.UtcNow), recordTransition: false);
        AddHistory(
            DiagnosticSeverity.Information,
            RecordingGuardianIssue.None,
            "Recording Guardian stopped.",
            null,
            null,
            null);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckCoreAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(_options.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (!await _checkGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var target = _context.Target;
            var isRecording = _context.IsRecording;
            if (_context.IsRecordingTransitioning)
            {
                Publish(Snapshot with
                {
                    CapturedAt = _clock.UtcNow,
                    State = RecordingGuardianState.Waiting,
                    Issue = RecordingGuardianIssue.None,
                    Message = "Recording state is changing; health checks are paused.",
                    TargetTitle = target?.Title
                });
                return;
            }

            if (!isRecording && target is null)
            {
                _evaluator.Reset();
                ResetRepairs();
                Publish(new RecordingGuardianSnapshot(
                    _clock.UtcNow,
                    RecordingGuardianState.Waiting,
                    RecordingGuardianIssue.None,
                    _context.IsAutomaticCaptureEnabled
                        ? "Watching for a selected window."
                        : "Ready; select a window or start recording.",
                    null,
                    0,
                    0,
                    0,
                    false,
                    null,
                    0,
                    null,
                    null,
                    0,
                    null));
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ProbeTimeout);
            ObsCaptureHealthSample sample;
            try
            {
                sample = await _healthClient.CaptureAsync(target, includePreview: true, timeout.Token);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                await PublishUnavailableAsync(
                    new TimeoutException($"OBS health check exceeded {_options.ProbeTimeout.TotalSeconds:0.#} seconds."),
                    target);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await PublishUnavailableAsync(ex, target);
                return;
            }

            RecordingFrameAnalysis? frame = null;
            if (sample.Preview is not null)
            {
                try
                {
                    frame = _frameAnalyzer.Analyze(sample.Preview);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Recording Guardian could not analyze the OBS preview frame.");
                }
            }

            var decision = _evaluator.Evaluate(sample, frame, target, isRecording);
            if (decision.HealthySampleCount >= _options.HealthyConfirmationSamples)
            {
                ResetRepairs();
            }

            var state = decision.Issue == RecordingGuardianIssue.None
                ? RecordingGuardianState.Healthy
                : decision.IsFault
                    ? RecordingGuardianState.Faulted
                    : RecordingGuardianState.Warning;
            var snapshot = BuildSnapshot(sample, frame, target, decision, state);
            Publish(snapshot);

            if (decision.RepairAction != RecordingGuardianRepairAction.None)
            {
                await TryRepairAsync(sample, frame, target, decision, cancellationToken);
            }
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task PublishUnavailableAsync(Exception exception, GameCaptureTarget? target)
    {
        var decision = _evaluator.EvaluateUnavailable(exception);
        var previous = Snapshot;
        var state = decision.IsFault
            ? RecordingGuardianState.Faulted
            : RecordingGuardianState.Warning;
        Publish(previous with
        {
            CapturedAt = _clock.UtcNow,
            State = state,
            Issue = decision.Issue,
            Message = decision.Message,
            TargetTitle = target?.Title,
            RepairAttempts = _repairAttempts,
            Evidence = decision.Evidence
        });
        _logger.LogWarning(exception, "Recording Guardian could not sample OBS.");
        await Task.CompletedTask;
    }

    private async Task TryRepairAsync(
        ObsCaptureHealthSample sample,
        RecordingFrameAnalysis? frame,
        GameCaptureTarget? target,
        RecordingGuardianDecision decision,
        CancellationToken cancellationToken)
    {
        var repairKey = $"{target?.CaptureBindingIdentity ?? "manual"}|{decision.Issue}|{decision.RepairAction}";
        if (!string.Equals(repairKey, _repairKey, StringComparison.Ordinal))
        {
            _repairKey = repairKey;
            _repairAttempts = 0;
            _lastRepairAt = null;
        }

        if (_repairAttempts >= _options.MaximumRepairAttempts)
        {
            var exhausted = BuildSnapshot(
                sample,
                frame,
                target,
                decision with
                {
                    Message = $"{decision.Message} Automatic repair needs attention.",
                    Evidence = $"{decision.Evidence} {_repairAttempts} automatic repair attempts were exhausted."
                },
                RecordingGuardianState.Faulted);
            Publish(exhausted);
            return;
        }

        if (_lastRepairAt is not null &&
            _clock.UtcNow - _lastRepairAt < _options.RepairCooldown)
        {
            return;
        }

        _repairAttempts++;
        _lastRepairAt = _clock.UtcNow;
        var actionText = DescribeAction(decision.RepairAction);
        Publish(BuildSnapshot(
            sample,
            frame,
            target,
            decision with { Message = $"{decision.Message} Applying automatic repair..." },
            RecordingGuardianState.Repairing));
        AddHistory(
            DiagnosticSeverity.Warning,
            decision.Issue,
            decision.Message,
            decision.Evidence,
            actionText,
            "Started");

        try
        {
            await _repairer.RepairAsync(decision.RepairAction, target, cancellationToken);
            var result = "The recorder accepted the repair; the next checks will verify it.";
            AddHistory(
                DiagnosticSeverity.Information,
                decision.Issue,
                "Recording Guardian repair completed.",
                decision.Evidence,
                actionText,
                result);
            _logger.LogWarning(
                "Recording Guardian applied {RepairAction} for {Issue}. Attempt {Attempt}/{MaximumAttempts}. Evidence: {Evidence}",
                decision.RepairAction,
                decision.Issue,
                _repairAttempts,
                _options.MaximumRepairAttempts,
                decision.Evidence);
            Publish(BuildSnapshot(
                sample,
                frame,
                target,
                decision with { Message = "Automatic repair applied; confirming capture health." },
                RecordingGuardianState.Warning));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var result = $"Repair failed: {ex.Message}";
            AddHistory(
                DiagnosticSeverity.Error,
                decision.Issue,
                "Recording Guardian repair failed.",
                decision.Evidence,
                actionText,
                result);
            _logger.LogError(
                ex,
                "Recording Guardian repair {RepairAction} failed for {Issue}.",
                decision.RepairAction,
                decision.Issue);
            Publish(BuildSnapshot(
                sample,
                frame,
                target,
                decision with
                {
                    Message = _repairAttempts >= _options.MaximumRepairAttempts
                        ? "Automatic repair failed and needs attention."
                        : "Automatic repair failed; Blackbox will retry.",
                    Evidence = $"{decision.Evidence} {result}"
                },
                _repairAttempts >= _options.MaximumRepairAttempts
                    ? RecordingGuardianState.Faulted
                    : RecordingGuardianState.Warning));
        }
    }

    private RecordingGuardianSnapshot BuildSnapshot(
        ObsCaptureHealthSample sample,
        RecordingFrameAnalysis? frame,
        GameCaptureTarget? target,
        RecordingGuardianDecision decision,
        RecordingGuardianState state) => new(
            sample.CapturedAt,
            state,
            decision.Issue,
            decision.Message,
            target?.Title,
            sample.OutputWidth,
            sample.OutputHeight,
            sample.FramesPerSecond,
            sample.RecordingOutputActive,
            sample.GameAudioPeakDb,
            sample.GameAudioSampleCount,
            frame,
            sample.Preview ?? (!sample.PreviewSampled && sample.SourceActive && sample.RecordingOutputActive && target?.Title == Snapshot.TargetTitle ? Snapshot.Preview : null),
            _repairAttempts,
            decision.Evidence)
        {
            MicrophoneCaptureEnabled = sample.MicrophoneCaptureEnabled,
            MicrophonePacketReceived = sample.MicrophonePacketReceived,
            MicrophonePacketCount = sample.MicrophonePacketCount
        };

    private void Publish(
        RecordingGuardianSnapshot snapshot,
        bool recordTransition = true)
    {
        var previous = Interlocked.Exchange(ref _snapshot, snapshot);
        if (recordTransition &&
            (previous.State != snapshot.State || previous.Issue != snapshot.Issue))
        {
            var severity = snapshot.State switch
            {
                RecordingGuardianState.Faulted => DiagnosticSeverity.Error,
                RecordingGuardianState.Warning or RecordingGuardianState.Repairing =>
                    DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Information
            };
            AddHistory(
                severity,
                snapshot.Issue,
                snapshot.Message,
                snapshot.Evidence,
                null,
                null);
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private void AddHistory(
        DiagnosticSeverity severity,
        RecordingGuardianIssue issue,
        string message,
        string? evidence,
        string? action,
        string? result)
    {
        lock (_historySync)
        {
            _history.Insert(0, new RecordingGuardianEvent(
                _clock.UtcNow,
                severity,
                issue,
                message,
                _context.Target?.Title ?? (_context.IsRecording ? "Manual capture" : "No active capture"),
                evidence,
                action,
                result));
            if (_history.Count > HistoryLimit)
            {
                _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
            }
        }

        var logMessage =
            "Recording Guardian: {GuardianMessage} Profile={ActiveProfile} Issue={Issue} Evidence={Evidence} Action={Action} Result={Result}";
        switch (severity)
        {
            case DiagnosticSeverity.Error:
                _logger.LogError(
                    logMessage,
                    message,
                    _context.Target?.Title ?? "None",
                    issue,
                    evidence,
                    action,
                    result);
                break;
            case DiagnosticSeverity.Warning:
                _logger.LogWarning(
                    logMessage,
                    message,
                    _context.Target?.Title ?? "None",
                    issue,
                    evidence,
                    action,
                    result);
                break;
            default:
                _logger.LogInformation(
                    logMessage,
                    message,
                    _context.Target?.Title ?? "None",
                    issue,
                    evidence,
                    action,
                    result);
                break;
        }
    }

    private void ResetRepairs()
    {
        _repairKey = null;
        _repairAttempts = 0;
        _lastRepairAt = null;
    }

    private static string DescribeAction(RecordingGuardianRepairAction action) =>
        action switch
        {
            RecordingGuardianRepairAction.RefreshCapture =>
                "Rebind the exact window, restore source visibility, and refit the canvas",
            RecordingGuardianRepairAction.RestartRecordingOutput =>
                "Restart the unexpectedly stopped OBS recording output",
            _ => "No automatic action"
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        await StopAsync();
        _checkGate.Dispose();
    }
}
