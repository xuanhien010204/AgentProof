# Changelog

All notable changes to AgentProof are documented here.

## [0.3.0-preview.2] - 2026-10-07

### Fixed

- Windows verification safely executes allowlisted `npm`, `npx`, `pnpm`, and `yarn` command shims.
- Workspace-scoped Build and Tests contracts no longer automatically execute unrelated workspaces.
- Scoped evidence preserves deterministic workspace identity and the existing evaluator semantics.
- Vitest is detected from package dependencies and standard configuration files.

### CI

- Added Windows Node execution regression coverage.

### Validation

- Dogfooded successfully against EducationCMS and ASRP.
- Unscoped contracts may still plan overlapping root/child aggregator workspaces; no heuristic deduplication was added.

## [0.3.0-preview.1] - 2026-10-07

### Added

- Deterministic `TaskContext`, `TaskContract`, and acceptance-criteria verification.
- Evidence Engine results with criterion status and evidence step references.
- Workspace-aware .NET and Node.js discovery and verification planning.
- Workspace-specific package-manager resolution and working directories.
- Workspace-scoped evidence requirements, including Browser/Playwright evidence.
- Local CLI and stdio MCP integration with four deterministic tools.
- .NET Tool packaging with reproducible CI artifacts.

### Security

- Preserved command allowlists, repository-bound working directories, timeout and cancellation handling, process-tree termination, and output secret redaction.

### Preview limitations

- Verification is local-first and does not install dependencies or SDKs automatically.
- Browser, database, runtime, and performance gaps remain explicit when no safe provider exists.
- NuGet.org publication is intentionally not enabled yet.
