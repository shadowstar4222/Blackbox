using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class WindowsGameProcessDetector(
    IRunningApplicationCatalog runningApplications,
    IGameProfileRepository gameProfiles,
    IGpuActivityProbe gpuActivityProbe,
    GpuActivityOptions gpuOptions,
    IClock clock,
    ILogger<WindowsGameProcessDetector> logger) : IGameProcessDetector
{
    private int _gpuUnavailableLogged;
    private string? _pendingHandoffProfileIdentity;
    private string? _pendingHandoffExecutableIdentity;
    private int _pendingHandoffDetections;

    public async Task<GameCaptureTarget?> DetectAsync(CancellationToken cancellationToken = default)
    {
        gpuOptions.Validate();
        var profiles = (await gameProfiles.GetAllAsync(cancellationToken))
            .Where(static profile => profile.AutomaticRecordingEnabled)
            .ToArray();
        if (profiles.Length == 0)
        {
            return null;
        }

        var running = await runningApplications.GetRunningApplicationsAsync(cancellationToken);
        var matches = FindMatches(running, profiles);
        if (matches.Count == 0)
        {
            ResetPendingHandoff();
            return null;
        }

        var gpuSnapshot = new GpuActivitySnapshot(false, new Dictionary<int, double>());
        if (matches.Any(static match => match.Profile.PreferGpuActivity))
        {
            gpuSnapshot = await gpuActivityProbe.SampleAsync(
                matches.Select(static match => match.Application.ProcessId).Distinct().ToArray(),
                cancellationToken);
            if (!gpuSnapshot.IsAvailable && Interlocked.Exchange(ref _gpuUnavailableLogged, 1) == 0)
            {
                logger.LogWarning("GPU activity corroboration is unavailable; executable and window matching will continue.");
            }
        }

        var selected = matches
            .Select(match => match with
            {
                GpuUtilization = gpuSnapshot.GetUtilization(match.Application.ProcessId)
            })
            .OrderByDescending(match =>
                match.Profile.PreferGpuActivity &&
                match.GpuUtilization >= gpuOptions.ActiveThresholdPercent)
            .ThenByDescending(static match => match.Application.IsForeground)
            .ThenByDescending(static match => match.MatchStrength)
            .ThenByDescending(static match =>
                (long)match.Application.WindowWidth * match.Application.WindowHeight)
            .First();

        var profile = selected.Profile;
        if (selected.IsLauncherHandoff && !profile.MatchesExecutablePath(selected.Application.ExecutablePath))
        {
            var handoffDetections = RegisterPendingHandoff(profile, selected.Application);
            if (handoffDetections >= 2)
            {
                profile = profile with
                {
                    ExecutableAliases = profile.ExecutableAliases
                        .Append(Path.GetFullPath(selected.Application.ExecutablePath))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    UpdatedAt = clock.UtcNow
                };
                await gameProfiles.UpsertAsync(profile, cancellationToken);
                logger.LogInformation(
                    "Learned launcher handoff for {GameName}: {LauncherPath} -> {GamePath}.",
                    profile.DisplayName,
                    profile.ExecutablePath,
                    selected.Application.ExecutablePath);
                ResetPendingHandoff();
            }
        }
        else
        {
            ResetPendingHandoff();
        }

        var sources = GameDetectionSource.ConfiguredExecutable | selected.AdditionalSources;
        if (profile.PreferGpuActivity &&
            selected.GpuUtilization >= gpuOptions.ActiveThresholdPercent)
        {
            sources |= GameDetectionSource.GpuActivity;
        }

        var target = selected.Application.ToCaptureTarget(sources) with
        {
            Title = profile.DisplayName,
            CaptureGameAudio = profile.CaptureGameAudio
        };
        logger.LogDebug(
            "Matched remembered game {GameName} at {Width}x{Height}. Sources={DetectionSources}, Gpu={GpuUtilization:F1}%.",
            profile.DisplayName,
            target.WindowWidth,
            target.WindowHeight,
            target.DetectionSources,
            selected.GpuUtilization);
        return target;
    }

    private IReadOnlyList<GameProfileMatch> FindMatches(
        IReadOnlyList<RunningApplication> applications,
        IReadOnlyList<GameProfile> profiles)
    {
        var matches = new List<GameProfileMatch>();
        foreach (var application in applications)
        {
            foreach (var profile in profiles)
            {
                if (profile.MatchesExecutablePath(application.ExecutablePath))
                {
                    var isAlias = profile.IsAlias(application.ExecutablePath);
                    matches.Add(new GameProfileMatch(
                        application,
                        profile,
                        isAlias ? GameDetectionSource.ExecutableAlias : GameDetectionSource.None,
                        isAlias ? 3 : 4,
                        IsLauncherHandoff: false));
                    continue;
                }

                var isPendingHandoff = profile.Identity == _pendingHandoffProfileIdentity &&
                    application.Identity == _pendingHandoffExecutableIdentity;
                if (profile.FollowLauncherHandoff &&
                    (isPendingHandoff || application.AncestorExecutableNames.Any(profile.ExecutableNames.Contains)))
                {
                    matches.Add(new GameProfileMatch(
                        application,
                        profile,
                        GameDetectionSource.ExecutableAlias | GameDetectionSource.LauncherHandoff,
                        MatchStrength: 2,
                        IsLauncherHandoff: true));
                }
            }
        }

        return matches;
    }

    private int RegisterPendingHandoff(GameProfile profile, RunningApplication application)
    {
        if (_pendingHandoffProfileIdentity == profile.Identity &&
            _pendingHandoffExecutableIdentity == application.Identity)
        {
            return ++_pendingHandoffDetections;
        }

        _pendingHandoffProfileIdentity = profile.Identity;
        _pendingHandoffExecutableIdentity = application.Identity;
        _pendingHandoffDetections = 1;
        return _pendingHandoffDetections;
    }

    private void ResetPendingHandoff()
    {
        _pendingHandoffProfileIdentity = null;
        _pendingHandoffExecutableIdentity = null;
        _pendingHandoffDetections = 0;
    }
}

internal sealed record GameProfileMatch(
    RunningApplication Application,
    GameProfile Profile,
    GameDetectionSource AdditionalSources,
    int MatchStrength,
    bool IsLauncherHandoff,
    double GpuUtilization = 0);
