using System.Text.RegularExpressions;

namespace Blackbox.Tests;

public sealed partial class XamlStaticResourceTests
{
    [Fact]
    public void Every_named_static_resource_reference_has_a_declared_key()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xamlFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "src", "Blackbox.App"),
            "*.xaml",
            SearchOption.AllDirectories);
        var declaredKeys = xamlFiles
            .SelectMany(file => KeyRegex().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var missing = xamlFiles
            .SelectMany(file => StaticResourceRegex()
                .Matches(File.ReadAllText(file))
                .Select(match => (File: Path.GetRelativePath(repositoryRoot, file),
                    Key: match.Groups["key"].Value)))
            .Where(reference =>
                !reference.Key.StartsWith('{') &&
                !declaredKeys.Contains(reference.Key))
            .Distinct()
            .OrderBy(static reference => reference.File, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Missing XAML StaticResource keys:\n" +
            string.Join(
                "\n",
                missing.Select(static reference => $"{reference.File}: {reference.Key}")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Blackbox.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Blackbox repository root.");
    }

    [GeneratedRegex("x:Key\\s*=\\s*\"(?<key>[^\"]+)\"")]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"\{StaticResource\s+(?<key>[^,\s}]+)")]
    private static partial Regex StaticResourceRegex();
}
