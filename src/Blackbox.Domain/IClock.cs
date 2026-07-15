namespace Blackbox.Domain;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
