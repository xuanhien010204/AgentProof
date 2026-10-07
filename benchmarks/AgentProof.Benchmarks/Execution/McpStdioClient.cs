using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Benchmarks.Model;
using AgentProof.Domain;
using AgentProof.Mcp;

namespace AgentProof.Benchmarks.Execution;

public sealed class McpStdioClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Process _process;
    private readonly Task<string> _stderrTask;
    private int _nextId = 1;
    private bool _disposed;

    private McpStdioClient(Process process)
    {
        _process = process;
        _stderrTask = _process.StandardError.ReadToEndAsync();
    }

    public static async Task<McpStdioClient> StartAsync(string? serverAssemblyPath = null, CancellationToken cancellationToken = default)
    {
        serverAssemblyPath ??= typeof(AgentProofTools).Assembly.Location;

        if (!File.Exists(serverAssemblyPath))
        {
            throw new FileNotFoundException($"AgentProof MCP server assembly not found at: {serverAssemblyPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(serverAssemblyPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start AgentProof MCP process: {serverAssemblyPath}");

        var client = new McpStdioClient(process);

        try
        {
            await client.InitializeProtocolAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeProtocolAsync(CancellationToken cancellationToken)
    {
        // 1. initialize
        var initId = _nextId++;
        await SendMessageAsync(new
        {
            jsonrpc = "2.0",
            id = initId,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "AgentProof.Benchmarks", version = "1.0" }
            }
        });

        var (initDoc, _, _) = await ReadResponseAsync(initId, cancellationToken);
        using (initDoc)
        {
            if (!initDoc.RootElement.TryGetProperty("result", out _))
            {
                throw new InvalidOperationException("Failed to initialize MCP server.");
            }
        }

        // 2. notifications/initialized
        await SendMessageAsync(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
            @params = new { }
        });
    }

    public async Task<BenchmarkOperationResult> BenchmarkInitializeAndListToolsAsync(CancellationToken cancellationToken = default)
    {
        var listId = _nextId++;
        var sw = Stopwatch.StartNew();

        await SendMessageAsync(new
        {
            jsonrpc = "2.0",
            id = listId,
            method = "tools/list",
            @params = new { }
        });

        var (doc, rawLine, rawBytes) = await ReadResponseAsync(listId, cancellationToken);
        sw.Stop();

        using (doc)
        {
            var isError = doc.RootElement.TryGetProperty("error", out var errElem);
            var errorMsg = isError ? errElem.ToString() : null;

            string toolResultText = string.Empty;
            if (!isError && doc.RootElement.TryGetProperty("result", out var resElem))
            {
                toolResultText = resElem.ToString();
            }

            var toolResultBytes = Encoding.UTF8.GetByteCount(toolResultText);

            return new BenchmarkOperationResult
            {
                Operation = "tools/list",
                DurationMs = sw.Elapsed.TotalMilliseconds,
                PayloadBytesUtf8 = toolResultBytes,
                PayloadCharacters = toolResultText.Length,
                JsonRpcPayloadBytes = rawBytes,
                ToolResultPayloadBytes = toolResultBytes,
                Success = !isError,
                Error = errorMsg
            };
        }
    }

    public async Task<BenchmarkOperationResult> CallToolAsync(
        string toolName,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        var id = _nextId++;
        var sw = Stopwatch.StartNew();

        await SendMessageAsync(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments
            }
        });

        var (doc, rawLine, rawBytes) = await ReadResponseAsync(id, cancellationToken);
        sw.Stop();

        using (doc)
        {
            var isRpcError = doc.RootElement.TryGetProperty("error", out var errElem);
            if (isRpcError)
            {
                var errorText = errElem.ToString();
                return new BenchmarkOperationResult
                {
                    Operation = toolName,
                    DurationMs = sw.Elapsed.TotalMilliseconds,
                    PayloadBytesUtf8 = Encoding.UTF8.GetByteCount(errorText),
                    PayloadCharacters = errorText.Length,
                    JsonRpcPayloadBytes = rawBytes,
                    ToolResultPayloadBytes = Encoding.UTF8.GetByteCount(errorText),
                    Success = false,
                    Error = errorText
                };
            }

            if (!doc.RootElement.TryGetProperty("result", out var resultElem))
            {
                return new BenchmarkOperationResult
                {
                    Operation = toolName,
                    DurationMs = sw.Elapsed.TotalMilliseconds,
                    PayloadBytesUtf8 = 0,
                    PayloadCharacters = 0,
                    JsonRpcPayloadBytes = rawBytes,
                    ToolResultPayloadBytes = 0,
                    Success = false,
                    Error = "Missing 'result' in response."
                };
            }

            bool isToolError = false;
            if (resultElem.TryGetProperty("isError", out var isErrProp) && isErrProp.GetBoolean())
            {
                isToolError = true;
            }

            // Extract content text
            var textBuilder = new StringBuilder();
            if (resultElem.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentElem.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var tProp))
                    {
                        var t = tProp.GetString();
                        if (t != null)
                        {
                            textBuilder.Append(t);
                        }
                    }
                }
            }

            var toolText = textBuilder.ToString();
            var toolBytes = (long)Encoding.UTF8.GetByteCount(toolText);
            var toolChars = toolText.Length;

            // Extract operation-specific domain metrics
            int? workspaceCount = null;
            int? technologyCount = null;
            int? frameworkCount = null;
            int? testFrameworkCount = null;
            int? recommendationCount = null;
            int? stepCount = null;
            int? evidenceCount = null;
            int? gapCount = null;
            string? verificationStatus = null;
            int? passedStepCount = null;
            int? failedStepCount = null;
            int? timedOutStepCount = null;

            if (!string.IsNullOrWhiteSpace(toolText))
            {
                try
                {
                    switch (toolName)
                    {
                        case "analyze_repository":
                            var profile = JsonSerializer.Deserialize<RepositoryProfile>(toolText, JsonOptions);
                            if (profile != null)
                            {
                                workspaceCount = profile.Workspaces?.Count ?? 0;
                                technologyCount = profile.Technologies?.Count ?? 0;
                                frameworkCount = profile.Frameworks?.Count ?? 0;
                                testFrameworkCount = profile.TestFrameworks?.Count ?? 0;
                            }
                            break;

                        case "recommend_skills":
                            var recs = JsonSerializer.Deserialize<List<SkillRecommendation>>(toolText, JsonOptions);
                            if (recs != null)
                            {
                                recommendationCount = recs.Count;
                            }
                            break;

                        case "create_verification_plan":
                            var plan = JsonSerializer.Deserialize<VerificationPlan>(toolText, JsonOptions);
                            if (plan != null)
                            {
                                workspaceCount = plan.WorkspaceIds?.Count ?? 0;
                                stepCount = plan.Steps?.Count ?? 0;
                                gapCount = plan.Gaps?.Count ?? 0;
                            }
                            break;

                        case "verify":
                            var vResult = JsonSerializer.Deserialize<VerificationResult>(toolText, JsonOptions);
                            if (vResult != null)
                            {
                                verificationStatus = vResult.Status.ToString();
                                evidenceCount = vResult.Evidence?.Count ?? 0;
                                gapCount = vResult.Gaps?.Count ?? 0;
                                passedStepCount = vResult.Evidence?.Count(e => e.Status == VerificationStepStatus.Passed) ?? 0;
                                failedStepCount = vResult.Evidence?.Count(e => e.Status == VerificationStepStatus.Failed) ?? 0;
                                timedOutStepCount = vResult.Evidence?.Count(e => e.Status == VerificationStepStatus.TimedOut) ?? 0;
                            }
                            break;
                    }
                }
                catch (Exception)
                {
                    // If parsing domain metrics fails, record that but keep payload measurement
                    if (!isToolError)
                    {
                        isToolError = true;
                    }
                }
            }

            return new BenchmarkOperationResult
            {
                Operation = toolName,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                PayloadBytesUtf8 = toolBytes,
                PayloadCharacters = toolChars,
                JsonRpcPayloadBytes = rawBytes,
                ToolResultPayloadBytes = toolBytes,
                Success = !isToolError,
                Error = isToolError ? (string.IsNullOrWhiteSpace(toolText) ? "Tool returned error." : toolText) : null,
                WorkspaceCount = workspaceCount,
                TechnologyCount = technologyCount,
                FrameworkCount = frameworkCount,
                TestFrameworkCount = testFrameworkCount,
                RecommendationCount = recommendationCount,
                StepCount = stepCount,
                EvidenceCount = evidenceCount,
                GapCount = gapCount,
                VerificationStatus = verificationStatus,
                PassedStepCount = passedStepCount,
                FailedStepCount = failedStepCount,
                TimedOutStepCount = timedOutStepCount
            };
        }
    }

    private async Task SendMessageAsync(object message)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await _process.StandardInput.WriteLineAsync(json);
        await _process.StandardInput.FlushAsync();
    }

    private async Task<(JsonDocument Document, string RawLine, long RawBytes)> ReadResponseAsync(
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new InvalidOperationException("AgentProof MCP server closed standard output unexpectedly.");

            if (string.IsNullOrWhiteSpace(line)) continue;

            var rawBytes = (long)Encoding.UTF8.GetByteCount(line);
            var document = JsonDocument.Parse(line);

            if (document.RootElement.TryGetProperty("id", out var idProp) && idProp.GetInt32() == expectedId)
            {
                return (document, line, rawBytes);
            }

            // Notification or different message
            document.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            await _process.WaitForExitAsync(CancellationToken.None);
            _ = await _stderrTask;
        }
        catch
        {
            // Ignore teardown exceptions
        }
        finally
        {
            _process.Dispose();
        }
    }
}
