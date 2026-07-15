namespace Blackbox.Domain;

public sealed record ObsConnectionSettings
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 4455;
    public string? Password { get; init; }
    public bool UseAuthentication => !string.IsNullOrWhiteSpace(Password);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException("OBS host is required.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("OBS websocket port must be between 1 and 65535.");
        }
    }
}
