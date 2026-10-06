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
        if (!Directory.Exists(Root)) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
