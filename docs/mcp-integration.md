# MCP Integration

AgentProof implements the Model Context Protocol (MCP) using the official C# SDK package `ModelContextProtocol` version 2.2.0 and its standard `stdio` transport.

Application logs are directed to `stderr` so they never interfere with JSON-RPC communication on `stdout`.

## Installation and Execution

### 1. Run as Installed .NET Tool (Recommended)

When AgentProof is installed as a global .NET Tool:

```shell
agentproof mcp
```

### 2. Run from Source / Build Output

Alternatively, run directly from the source repository:

```shell
dotnet run --project src/AgentProof.Mcp
```

Or execute the compiled binary directly:

```shell
dotnet path/to/AgentProof.Mcp.dll
```

## Host Configuration

### Standard MCP Configuration (`mcpServers`)

For hosts that support standard stdio MCP configuration (such as Antigravity, Claude Desktop, Cursor, and generic MCP clients):

#### When installed globally as a .NET Tool:

```json
{
  "mcpServers": {
    "agentproof": {
      "command": "agentproof",
      "args": ["mcp"]
    }
  }
}
```

#### When running from source:

```json
{
  "mcpServers": {
    "agentproof": {
      "command": "dotnet",
      "args": ["run", "--project", "D:/project/AgentProof/src/AgentProof.Mcp"]
    }
  }
}
```

### Codex and Antigravity Host Notes

- **Antigravity**: Verified via stdio subprocess transport with standard tool definitions.
- **Codex**: Launch via standard stdio command `agentproof mcp` or through the host's supported MCP client bridge.

## Tool Surface

AgentProof exposes exactly four tools:

| Tool | Mode | Description |
|---|---|---|
| `analyze_repository` | Read-only | Returns repository metadata, detected technologies, frameworks, workspaces, and test runners (`schemaVersion: 1`). |
| `recommend_skills` | Read-only | Provides deterministic skill recommendations based on repository facts and `TaskContext` (`schemaVersion: 1`). |
| `create_verification_plan` | Read-only | Generates a deterministic sequence of build, test, or lint steps based on actual project configurations (`schemaVersion: 1`). |
| `verify` | Read/Write (Safe) | Executes approved verification steps in their respective working directories and returns evidence and status. Supports `detailLevel` argument: `'compact'` (default context-efficient summary) or `'full'` (complete per-step diagnostics). |

AgentProof intentionally **does not** expose arbitrary command execution, code editing, shell execution, or package installation tools.

### Detail Levels and Progressive Disclosure

- **`compact` (default)**: Returns a context-efficient summary (`schemaVersion: 1`). Failed and timed-out steps include explicit failure reasons and command error output summaries directly in `failedSteps`. Successful step stdout and durations are omitted to minimize context token usage.
- **`full`**: Returns the identical verification results, summary accounting, and status, and additionally includes an `evidence` array containing complete per-step execution evidence (`status`, `exitCode`, `durationMs`, and full `outputSummary`).

```text
verify(..., detailLevel: "compact") [Default]
  ↓ (Context-efficient summary with failure diagnostics)
Task completed OR fix failure indicated in failedSteps
  ↓ (Optional: inspect full execution logs)
verify(..., detailLevel: "full")
```

## Expected Agent Workflow

```text
Host receives task intent
  ↓
analyze_repository
  ↓
recommend_skills
  ↓
create_verification_plan
  ↓
Host implements code changes
  ↓
verify
  ↓
Verification Status?
  ├─ Verified → Task complete (DONE)
  ├─ NotVerified → Fix issues → verify again
  └─ PartiallyVerified → Surface explicit gap to user / host
```

## Verification Status Semantics

- **`Verified`**: All required verification steps passed with exit code `0` and no verification gaps exist. The agent may claim task completion.
- **`NotVerified`**: One or more required verification checks failed (e.g. build failure or failing test). The agent must inspect failure output, make fixes, and rerun verification before claiming completion.
- **`PartiallyVerified`**: All executed steps passed, but explicit evidence gaps remain (such as requiring `Browser` evidence when Playwright is not installed, or requiring `Database` evidence without a database engine). The agent must surface the verification gap rather than falsely claiming full verification.

## Common Issues & Troubleshooting

1. **Process Locking during Builds**:
   If the MCP server is actively executing within a directory being rebuilt by another process, stop the running MCP host process or allow it to finish before rebuilding.
2. **Missing Prerequisite Runtimes**:
   AgentProof relies on the locally installed toolchains (`dotnet`, `npm`, `pnpm`, `yarn`, `npx`). Ensure the relevant CLI tools for your repository are present in `PATH`.
3. **Working Directory & Path Containment**:
   All verification commands are restricted to paths within the repository root. Commands attempting directory traversal (`../`) are automatically rejected.
