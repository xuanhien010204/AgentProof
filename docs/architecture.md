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

Solutions are preferred over individual projects in the same area. Node workspaces are discovered from each non-generated-directory `package.json`. Generated directories (`.git`, `bin`, `obj`, `node_modules`, `.next`, `dist`, `build`, `coverage`, and `TestResults`) are excluded.

Package managers are resolved per Node workspace using local lockfiles. The deterministic precedence is `package-lock.json` → npm, `pnpm-lock.yaml` → pnpm, and `yarn.lock` → Yarn. If no local lockfile exists, npm is used as the existing safe default; lockfiles in sibling workspaces do not affect that choice.

Verification steps run from their workspace directory. Nested step IDs are prefixed with a sanitized workspace ID (for example, `backend:dotnet-build`); root-only repositories retain the legacy IDs for compatibility. Evidence remains repository-wide by `EvidenceType`; criteria are not yet semantically scoped to a workspace. Workspace-scoped evidence requirements are deferred to Phase 3.5.
