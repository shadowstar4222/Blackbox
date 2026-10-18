namespace Blackbox.Infrastructure;

public interface IObsPortableConfigurationContributor
{
    void Apply(string portableRootDirectory);
}
