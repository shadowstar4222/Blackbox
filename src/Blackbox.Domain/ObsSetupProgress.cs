namespace Blackbox.Domain;

public enum ObsSetupStage
{
    CheckingInstallation,
    CopyingExistingInstallation,
    Downloading,
    Verifying,
    Extracting,
    Launching,
    Connecting,
    Configuring,
    TestingRecording,
    Complete
}

public sealed record ObsSetupProgress(ObsSetupStage Stage, string Message, int? Percent = null);
