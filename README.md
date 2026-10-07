# AgentProof

AgentProof is a local-first engineering assurance layer for AI coding agents.

AI agents such as Codex or Antigravity provide reasoning and implementation. AgentProof provides deterministic repository facts, engineering rules, skill recommendations, verification plans, executable verification, and evidence.

```text
Developer
    ↓
Codex / Antigravity
    ↓ MCP
AgentProof
    ↓
Local Repository
```

Code generation is not evidence of correctness. AgentProof reports `Verified` only when every required generated check succeeds and no verification gap remains.

AgentProof `0.3.0-preview.1` is the current public preview, available from NuGet.org and as a GitHub prerelease. The preview is local-first.

## Public preview scope

AgentProof does not require an OpenAI API key, ChatGPT API, Claude API, Gemini API, or any external LLM API. The reasoning model comes from the MCP host agent. Repository analysis and verification run locally.

The solution contains:

- `AgentProof.Domain`: immutable domain concepts and verification evidence.
- `AgentProof.Application`: use-case orchestration, skill rules, and verification planning.
- `AgentProof.Infrastructure`: local repository inspection, project configuration reading, and safe process execution.
- `AgentProof.Mcp`: the primary stdio MCP adapter.
- `AgentProof.Cli`: a small development and debugging adapter.
- Unit and integration test projects.

See [architecture](docs/architecture.md), [MCP integration](docs/mcp-integration.md), and [security](docs/security.md) for details.
Release history is tracked in [CHANGELOG.md](CHANGELOG.md).

## Requirements

- .NET 10 SDK
- Git is optional; its presence is reported during analysis
- Node.js package-manager tooling is required only when verifying Node.js workspaces.

## Build and test

```shell
dotnet restore AgentProof.sln
dotnet build AgentProof.sln
dotnet test AgentProof.sln
```

## Install as .NET Tool

### NuGet.org installation

```shell
dotnet tool install --global AgentProof --version 0.3.0-preview.1
```

### GitHub Release or local package installation

The GitHub Release and local package paths are alternatives to the NuGet.org installation:

```shell
dotnet tool install --global AgentProof --add-source <local-package-directory> --version 0.3.0-preview.1
```

Update or remove the installed tool with:

```shell
dotnet tool update --global AgentProof
dotnet tool uninstall --global AgentProof
```

### Usage

```shell
agentproof --help
agentproof analyze .
agentproof recommend . --task-context <task.json>
agentproof plan . --task-context <task.json>
agentproof verify . --task-context <task.json>
agentproof mcp
```

`recommend`, `plan`, and `verify` require `--task-context <task.json>`.

## CLI debugging

```shell
dotnet run --project src/AgentProof.Cli -- analyze .
dotnet run --project src/AgentProof.Cli -- recommend . --task-context task.json
dotnet run --project src/AgentProof.Cli -- plan . --task-context task.json
dotnet run --project src/AgentProof.Cli -- verify . --task-context task.json
dotnet run --project src/AgentProof.Cli -- mcp
```

Exit codes:
- `0`: `Verified` (or successful command execution for analyze/recommend/plan)
- `1`: CLI, input, or runtime error
- `2`: `NotVerified` (a required verification check failed)
- `3`: `PartiallyVerified` (checks passed, but explicit verification gaps remain)

Example task files are available in [`docs/examples`](docs/examples). A task context looks like:

```json
{
  "description": "Fix null handling in repository analyzer",
  "taskType": "BugFix",
  "complexity": "Low",
  "affectedAreas": ["Backend"],
  "hasUiChanges": false,
  "hasDatabaseChanges": false,
  "hasArchitectureChanges": false,
  "hasBrowserVisibleChanges": false,
  "hasDeploymentChanges": false
}
```

## MCP server

The server uses standard stdio JSON-RPC transport and the official `ModelContextProtocol` 2.2.0 C# SDK.

Launch via global tool:

```shell
agentproof mcp
```

Or run directly from source:

```shell
dotnet run --project src/AgentProof.Mcp
```

It exposes four tools:

- `analyze_repository`
- `recommend_skills`
- `create_verification_plan`
- `verify`

The server intentionally does not expose code generation, file editing, package installation, or arbitrary command execution.

## Current limitations

- Browser verification requires an existing Playwright installation and is reported as a gap otherwise.
- Repository discovery supports nested .NET solutions/projects and independent Node workspaces. Acceptance criteria can require evidence from a specific deterministic workspace; unscoped requirements preserve the existing all-planned-providers behavior.
- Verification trusts the local repository being checked. Build and test systems can execute repository-defined hooks; see the security document.
- Stdio transport only; no persistence, UI, telemetry, or cloud service.

## License

MIT. See [LICENSE](LICENSE).
