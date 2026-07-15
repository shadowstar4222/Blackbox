namespace Blackbox.Export;

public sealed record FfmpegInstallation(
    string RootDirectory,
    string FfmpegPath,
    string FfprobePath,
    string FfplayPath);
