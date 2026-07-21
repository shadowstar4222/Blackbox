namespace Blackbox.Infrastructure;

public interface IProcessExecutablePathResolver
{
    string? Resolve(int processId, string executableName);
}
