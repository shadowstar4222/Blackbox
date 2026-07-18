using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Blackbox.Infrastructure;

public sealed class WindowsStartupManager : IWindowsStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Blackbox";
    private readonly string _startupCommand;
    private readonly IStartupRegistry _registry;

    public WindowsStartupManager()
        : this(
            Environment.ProcessPath
                ?? throw new InvalidOperationException("Windows did not provide the Blackbox executable path."),
            CreateRegistry())
    {
    }

    internal WindowsStartupManager(string executablePath, IStartupRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(registry);
        _startupCommand = BuildStartupCommand(executablePath);
        _registry = registry;
    }

    public bool IsEnabled =>
        string.Equals(_registry.Read(), _startupCommand, StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _registry.Write(_startupCommand);
        }
        else
        {
            _registry.Delete();
        }
    }

    internal static string BuildStartupCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{Path.GetFullPath(executablePath)}\" --background";
    }

    private static IStartupRegistry CreateRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows startup registration requires Windows.");
        }

        return new CurrentUserStartupRegistry();
    }

    internal interface IStartupRegistry
    {
        string? Read();
        void Write(string command);
        void Delete();
    }

    [SupportedOSPlatform("windows")]
    private sealed class CurrentUserStartupRegistry : IStartupRegistry
    {
        public string? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }

        public void Write(string command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Windows startup settings are unavailable.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }

        public void Delete()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
