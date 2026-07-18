namespace Blackbox.Infrastructure;

public interface IWindowsStartupManager
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}
