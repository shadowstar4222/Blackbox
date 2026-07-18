namespace Blackbox.Infrastructure;

public sealed record UserExperienceOptions
{
    public required string SettingsPath { get; init; }
}
