namespace Blackbox.Domain;

public sealed record ObsConnectionStatus(bool IsConnected, string Message)
{
    public static ObsConnectionStatus Connected(string message = "Connected to OBS.") => new(true, message);
    public static ObsConnectionStatus Failed(string message) => new(false, message);
}
