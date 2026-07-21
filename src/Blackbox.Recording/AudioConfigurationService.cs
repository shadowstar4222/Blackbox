using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class AudioConfigurationService(
    IObsController obsController,
    MicrophoneSelectionService microphoneSelectionService,
    ILogger<AudioConfigurationService> logger)
{
    public async Task ApplyAsync(
        AudioRoutingProfile profile,
        MicrophoneProcessingSettings microphoneSettings,
        CancellationToken cancellationToken = default)
    {
        profile.Validate();
        microphoneSettings.Validate();
        await obsController.ConfigureAudioAsync(profile, microphoneSettings, cancellationToken);
        var microphone = await microphoneSelectionService.ApplyAsync(cancellationToken);
        logger.LogInformation(
            "Applied Blackbox audio routing profile with microphone {MicrophoneName}.",
            microphone.Name);
    }
}
