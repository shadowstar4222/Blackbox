namespace Blackbox.Infrastructure;

public interface IDefaultMicrophoneProvider
{
    string? GetDefaultDeviceId();
}
