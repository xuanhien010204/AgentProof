using System.Diagnostics;
using System.Text.Json;
using AgentProof.Mcp;

namespace AgentProof.IntegrationTests;

public sealed class McpDiscoveryTests
{
    private static readonly string[] ExpectedTools =
        ["analyze_repository", "recommend_skills", "create_verification_plan", "verify"];

    [Fact]
    public async Task StdioServerStartsAndDiscoversExpectedTools()
    {
        var serverAssembly = typeof(AgentProofTools).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(serverAssembly);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start MCP server.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "AgentProof.Tests", version = "1.0" }
                }
            });
            using var initialize = await ReadResponseAsync(process, 1, timeout.Token);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } });
            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
            using var list = await ReadResponseAsync(process, 2, timeout.Token);
            var names = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString()).ToArray();
            Assert.Equal(
                ExpectedTools.OrderBy(x => x, StringComparer.Ordinal),
                names.OrderBy(x => x, StringComparer.Ordinal));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await stderr;
        }
    }

    [Fact]
    public async Task CliMcpOptionStartsAndDiscoversExpectedTools()
    {
        var cliAssembly = typeof(AgentProof.Cli.Program).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(cliAssembly);
        startInfo.ArgumentList.Add("mcp");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start MCP server via CLI.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "AgentProof.Tests", version = "1.0" }
                }
            });
            using var initialize = await ReadResponseAsync(process, 1, timeout.Token);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } });
            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
            using var list = await ReadResponseAsync(process, 2, timeout.Token);
            var names = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString()).ToArray();
            Assert.Equal(
                ExpectedTools.OrderBy(x => x, StringComparer.Ordinal),
                names.OrderBy(x => x, StringComparer.Ordinal));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await stderr;
        }
    }

    [Fact]
    public async Task ToolsListExposesVerifyDetailLevelArgumentCorrectly()
    {
        var serverAssembly = typeof(AgentProofTools).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(serverAssembly);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start MCP server.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "AgentProof.Tests", version = "1.0" }
                }
            });
            using var initialize = await ReadResponseAsync(process, 1, timeout.Token);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } });
            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
            using var list = await ReadResponseAsync(process, 2, timeout.Token);
            var tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray();
            var verifyTool = tools.Single(x => x.GetProperty("name").GetString() == "verify");
            var inputSchema = verifyTool.GetProperty("inputSchema");
            var properties = inputSchema.GetProperty("properties");
            Assert.True(properties.TryGetProperty("detailLevel", out var detailLevelProp));
            Assert.Contains("compact", detailLevelProp.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("full", detailLevelProp.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
            var required = inputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Contains("repositoryPath", required);
            Assert.DoesNotContain("detailLevel", required);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await stderr;
        }
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new InvalidOperationException("MCP server closed stdout before responding.");
            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                return document;
            document.Dispose();
        }
    }
}
