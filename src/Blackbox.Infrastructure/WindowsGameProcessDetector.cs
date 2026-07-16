using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class WindowsGameProcessDetector(
    IRunningApplicationCatalog runningApplications,
    IGameProfileRepository gameProfiles,
    ILogger<WindowsGameProcessDetector> logger) : IGameProcessDetector
{
    public async Task<GameCaptureTarget?> DetectAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await gameProfiles.GetAllAsync(cancellationToken);
        var enabledProfiles = profiles
            .Where(profile => profile.AutomaticRecordingEnabled)
            .ToDictionary(profile => profile.Identity, StringComparer.OrdinalIgnoreCase);
        if (enabledProfiles.Count == 0)
        {
            return null;
        }

        var running = await runningApplications.GetRunningApplicationsAsync(cancellationToken);
        var match = running
            .Where(application => enabledProfiles.ContainsKey(application.Identity))
            .OrderByDescending(application => application.IsForeground)
            .ThenByDescending(application => (long)application.WindowWidth * application.WindowHeight)
            .FirstOrDefault();
        if (match is null)
        {
            return null;
        }

        var profile = enabledProfiles[match.Identity];
        var target = match.ToCaptureTarget(GameDetectionSource.ConfiguredExecutable) with
        {
            Title = profile.DisplayName
        };
        logger.LogDebug(
            "Matched remembered game {GameName} at {Width}x{Height}.",
            profile.DisplayName,
            target.WindowWidth,
            target.WindowHeight);
        return target;
    }
}
