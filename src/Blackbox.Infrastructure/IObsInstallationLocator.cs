using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public interface IObsInstallationLocator
{
    ObsInstallation? FindExistingInstallation();
}
