using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public interface IObsConnectionSettingsProvider
{
    ObsConnectionSettings Current { get; }
    void Set(ObsConnectionSettings settings);
}
