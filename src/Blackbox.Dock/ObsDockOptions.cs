namespace Blackbox.Dock;

public sealed class ObsDockOptions
{
    public string SettingsPath { get; init; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SettingsPath))
        {
            throw new InvalidOperationException("The OBS dock settings path is required.");
        }
    }
}
