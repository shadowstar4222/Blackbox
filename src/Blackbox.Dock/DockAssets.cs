using System.Reflection;

namespace Blackbox.Dock;

internal static class DockAssets
{
    public static byte[] Read(string name)
    {
        var resourceName = $"Blackbox.Dock.Assets.{name}";
        using var stream = typeof(DockAssets).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded OBS dock asset '{name}' is missing.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
