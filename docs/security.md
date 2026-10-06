# Security

AgentProof V0.1 runs locally. It does not upload repository source, call an LLM API, require API keys, or provide telemetry.

## Analysis

- MCP analysis and planning tools are read-only.
- Repository paths are normalized and must exist.
- Generated directories such as `.git`, `bin`, `obj`, `node_modules`, `.next`, `dist`, `build`, `coverage`, and `TestResults` are pruned.
- Only small known text configuration files are parsed; binary source content is not inspected.

## Verification execution

- There is no arbitrary shell MCP tool.
- `verify` regenerates its plan internally rather than accepting a caller-provided command.
- Commands are represented as an executable plus argument list.
- `UseShellExecute` is disabled; AgentProof does not invoke `cmd.exe /c`, PowerShell, or `bash -c`.
- The runner accepts only generated `dotnet restore/build/test`, known package scripts, and an installed Playwright test command.
- Solution and project targets and working directories must remain inside the repository root.
- Each process has a timeout and supports cancellation.
- The process tree is terminated on timeout or caller cancellation before propagating cancellation.
- stdout and stderr are captured with a 64 KiB-per-stream limit.
- Common secret assignments are redacted from returned output, and environment variables are never enumerated or logged.

## Trust boundary

Build and test tools can execute scripts defined by the repository itself, including MSBuild targets and package lifecycle hooks. AgentProof prevents the MCP caller from selecting an arbitrary command, but it does not sandbox a repository's own build system. Run verification only against repositories you trust. OS-level sandboxing is a future hardening option.

## Result semantics

- `Verified`: all required checks passed and there are no known gaps.
- `NotVerified`: a required check failed or timed out.
- `PartiallyVerified`: executed checks passed, but an optional failure or explicit verification gap remains.
