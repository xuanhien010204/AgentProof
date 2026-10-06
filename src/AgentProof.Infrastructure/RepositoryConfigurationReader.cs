using System.Text.Json;
using AgentProof.Application;

namespace AgentProof.Infrastructure;

public sealed class RepositoryConfigurationReader : IRepositoryConfigurationReader
{
    public string? FindDotNetEntryPoint(string repositoryPath)
    {
        var root = LocalRepositoryAnalyzer.NormalizeExistingDirectory(repositoryPath);
        var solution = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (solution is not null) return Path.GetRelativePath(root, solution);

        var project = Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return project is null ? null : Path.GetRelativePath(root, project);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadPackageScriptsAsync(
        string repositoryPath, CancellationToken cancellationToken = default)
    {
        var root = LocalRepositoryAnalyzer.NormalizeExistingDirectory(repositoryPath);
        var path = Path.Combine(root, "package.json");
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("scripts", out var element) || element.ValueKind != JsonValueKind.Object)
            return scripts;

        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String)
                scripts[property.Name] = property.Value.GetString() ?? string.Empty;
        return scripts;
    }
}
