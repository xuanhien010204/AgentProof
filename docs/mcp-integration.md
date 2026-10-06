# MCP integration

AgentProof uses the official MCP C# SDK package `ModelContextProtocol` version 2.2.0 and its stdio server transport. The implementation follows the official API:

```csharp
services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
```

Tool classes use `[McpServerToolType]`; tool methods use `[McpServerTool]`.

## Build and executable

Build a release server:

```shell
dotnet build src/AgentProof.Mcp/AgentProof.Mcp.csproj -c Release
```

Host-neutral stdio launch details:

- Executable: `dotnet`
- Arguments: the absolute path to `src/AgentProof.Mcp/bin/Release/net10.0/AgentProof.Mcp.dll`
- Transport: stdio
- Environment variables: none required

All protocol output goes to stdout. Application logs are directed to stderr so they do not corrupt MCP messages.

## Tools

| Tool | Behavior |
|---|---|
| `analyze_repository` | Read-only repository analysis |
| `recommend_skills` | Deterministic recommendations from repository facts and `TaskContext` |
| `create_verification_plan` | Safe steps based on actual solution and package scripts |
| `verify` | Internally plans, executes approved checks, and returns evidence |

## Codex and Antigravity

Both hosts should launch the executable above as a local stdio MCP server. Host-specific configuration formats are intentionally marked **TODO** for V0.1: no currently verified official Codex or Antigravity configuration format was established during implementation, so this project does not publish speculative JSON or settings keys.

After configuration, confirm that tool discovery returns exactly the four tools listed above. The integration test performs this discovery against the real server process.

## Expected flow

```text
Host analyzes user intent
  → analyze_repository
  → recommend_skills
  → create_verification_plan
  → host implements changes
  → verify
  → fix and repeat on failure
```

The host should claim completion only when the result is `Verified`. `PartiallyVerified` identifies an explicit gap such as unavailable browser verification.
