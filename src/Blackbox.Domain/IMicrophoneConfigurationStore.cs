namespace Blackbox.Domain;

public interface IMicrophoneConfigurationStore
{
    MicrophoneConfiguration Current { get; }
    void Save(MicrophoneConfiguration configuration);
}
