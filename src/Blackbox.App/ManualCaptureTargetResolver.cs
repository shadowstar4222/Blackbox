using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.App;

public sealed class ManualCaptureTargetResolver(
    IGameProcessDetector detector,
    IGameCaptureSelectionStore selectionStore,
    IGameProfileRepository profiles,
    IRunningApplicationCatalog applications,
    IObsController recorder)
{
    private GameCaptureTarget? _temporaryTarget;
    internal GameCaptureTarget? TemporaryTarget => Volatile.Read(ref _temporaryTarget);
    internal void SetTemporaryTarget(GameCaptureTarget? target) => Volatile.Write(ref _temporaryTarget, target);

    public async Task<GameCaptureTarget?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (TemporaryTarget is { } temporary)
        {
            var window = await applications.GetRunningApplicationAsync(temporary.WindowHandle, cancellationToken);
            return window is not null && window.ProcessId == temporary.ProcessId &&
                window.Identity == temporary.Identity && window.IsCapturable && !window.IsMinimized &&
                ReferenceEquals(temporary, TemporaryTarget)
                ? window.ToCaptureTarget(GameDetectionSource.ConfiguredExecutable) with
                    { Title = temporary.Title, CaptureGameAudio = temporary.CaptureGameAudio }
                : null;
        }
        var selection = selectionStore.Current;
        if (selection is null)
        {
            return null;
        }

        var profile = (await profiles.GetAllAsync(cancellationToken))
            .FirstOrDefault(candidate => candidate.Identity == selection.ProfileIdentity);
        if (profile is null)
        {
            return null;
        }

        var detected = await detector.DetectAsync(cancellationToken);
        var selectedWindow = (recorder as INativeRecorderController)?.Target;
        if (selectedWindow is not null && selectedWindow.Identity == selection.TargetIdentity)
        {
            var window = (await applications.GetRunningApplicationsAsync(cancellationToken))
                .FirstOrDefault(application => application.WindowHandle == selectedWindow.WindowHandle &&
                    application.ProcessId == selectedWindow.ProcessId &&
                    application.Identity == selection.TargetIdentity);
            if (window is null || !profile.MatchesExecutablePath(window.ExecutablePath))
            {
                return null;
            }

            var selectedTarget = window.ToCaptureTarget(GameDetectionSource.ConfiguredExecutable) with
            {
                Title = profile.DisplayName,
                CaptureGameAudio = profile.CaptureGameAudio
            };
            return selectionStore.Current == selection && IsUsable(selectedTarget) ? selectedTarget : null;
        }

        if (detected is not null && detected.Identity == selection.TargetIdentity &&
            profile.MatchesExecutablePath(detected.ExecutablePath) && IsUsable(detected))
        {
            return selectionStore.Current == selection ? detected : null;
        }

        // Manual selection remains available when automatic recording is disabled for the profile.
        var running = (await applications.GetRunningApplicationsAsync(cancellationToken))
            .Where(application => application.Identity == selection.TargetIdentity &&
                profile.MatchesExecutablePath(application.ExecutablePath) &&
                application.IsCapturable && !application.IsMinimized &&
                application.WindowWidth > 0 && application.WindowHeight > 0)
            .OrderByDescending(static application => application.IsForeground)
            .ThenByDescending(static application => (long)application.WindowWidth * application.WindowHeight)
            .FirstOrDefault();
        if (running is null)
        {
            return null;
        }

        var target = running.ToCaptureTarget(GameDetectionSource.ConfiguredExecutable) with
        {
            Title = profile.DisplayName,
            CaptureGameAudio = profile.CaptureGameAudio
        };
        return selectionStore.Current == selection && IsUsable(target) ? target : null;
    }

    private static bool IsUsable(GameCaptureTarget target) =>
        target.IsCapturable && !target.IsMinimized && target.ProcessId > 0 &&
        target.WindowHandle != 0 && target.WindowWidth > 0 && target.WindowHeight > 0;
}
