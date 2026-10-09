# Architecture

AgentProof follows a small Clean Architecture dependency graph:

```text
AgentProof.Domain
        ↑
AgentProof.Application
        ↑
AgentProof.Infrastructure

AgentProof.Mcp ─┐
AgentProof.Cli ─┴─ composition and entry-point adapters
```

## Layers

### Domain

Contains repository profiles, structured task context, skill recommendations, verification plans, evidence, and statuses. It has no dependency on infrastructure, MCP, CLI, or an external SDK.

### Application

Defines `IRepositoryAnalyzer`, `ISkillRecommender`, `IRepositoryConfigurationReader`, `IVerificationPlanner`, and `IVerificationRunner`. It contains deterministic recommendation and verification-planning rules plus the shared `AgentProofService` use-case facade.

### Infrastructure

Implements local filesystem inspection, project configuration reads, and verification process execution. Generated directories are pruned during traversal. Commands use `ProcessStartInfo` without a shell.

### MCP

The primary adapter. It maps four MCP tools to `AgentProofService` and contains no recommendation, planning, or verification business rules.

### CLI

A debugging adapter that invokes the same `AgentProofService`. It does not duplicate MCP or core logic.

## Dependency rule

Dependencies point inward. Domain is independent, Application depends only on Domain, and Infrastructure implements Application boundaries. Entry points compose the graph.

The AgentProof core is independent of Codex, Antigravity, Claude, VS Code, and any future desktop UI. Future adapters must reuse the existing Application use cases rather than duplicate them.

## Task-analysis boundary

The host AI agent translates natural-language intent into `TaskContext`. AgentProof does not attempt to reproduce LLM reasoning. It applies deterministic rules to the supplied task context and observed repository profile.

## Workspace-aware discovery

`RepositoryProfile.Workspaces` contains independently verifiable repository areas. A workspace has a stable relative ID/path, local technologies, frameworks, test frameworks, package scripts, package manager, and (when applicable) a solution or project entry point.

Discovery uses configuration boundaries rather than treating every project file as a workspace. A `.sln` or `.slnx` is the primary .NET boundary; projects listed by that solution contribute their facts to the solution workspace. When no solution represents a project, explicit `ProjectReference` relationships group connected standalone projects, while unrelated projects remain separate. A standalone `.csproj` is otherwise its own workspace. Node workspaces are discovered from each non-generated-directory `package.json`, and Python `requirements.txt`/`pyproject.toml` plus Flutter `pubspec.yaml` configurations are detectable even though no Python or Flutter verification provider is currently emitted. Generated directories (`.git`, `bin`, `obj`, `node_modules`, `.next`, `dist`, `build`, `coverage`, and `TestResults`) and nested dot-directories are excluded; the repository root itself is always eligible for root configuration files.

Package managers are resolved per Node workspace using local lockfiles. The deterministic precedence is `package-lock.json` → npm, `pnpm-lock.yaml` → pnpm, and `yarn.lock` → Yarn. If no local lockfile exists, npm is used as the existing safe default; lockfiles in sibling workspaces do not affect that choice.

Verification steps run from their workspace directory and carry the logical workspace ID. Nested step IDs are prefixed with a sanitized workspace ID (for example, `backend:dotnet-build`); root-only repositories retain the legacy IDs for compatibility.

`EvidenceRequirement` adds optional workspace scope to acceptance criteria and task-level requirements. A null `WorkspaceId` preserves the existing repository-wide rule: every planned provider for that evidence type must execute and pass. A non-null ID must match a discovered workspace exactly, and every planned provider of that type in that workspace must execute and pass. An unknown workspace produces `MISSING_WORKSPACE_EVIDENCE_PROVIDER`; it never falls back to another workspace. Criterion results continue to reference the actual matching step IDs.
