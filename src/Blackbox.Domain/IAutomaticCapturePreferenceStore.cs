namespace Blackbox.Domain;

public interface IAutomaticCapturePreferenceStore
{
    bool WasEnabled { get; }
    void Save(bool enabled);
}
