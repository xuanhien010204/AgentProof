namespace AgentProof.UnitTests;

internal sealed class TemporaryRepository : IDisposable
{
    public TemporaryRepository() => Root = Path.Combine(Path.GetTempPath(), "AgentProof.Tests", Guid.NewGuid().ToString("N"));
    public string Root { get; }

    public string Write(string relativePath, string content = "")
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
