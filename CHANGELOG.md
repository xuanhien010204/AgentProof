# Changelog

All notable changes to AgentProof are documented here.

## [0.3.0-preview.3] - 2026-10-10

### Fixed

- **MCP Input Validation Order**: Validates `detailLevel` argument strictly prior to process execution in `verify`, rejecting invalid, empty, or whitespace inputs with clear errors before build or test commands execute.
- **Test Execution Assurance**: Enforces test discovery and execution pattern verification across VSTest, Microsoft.Testing.Platform, Node TAP, Jest, Vitest, and Mocha runners. Zero discovered or executed tests fail with `"No tests were executed (zero tests discovered or executed)."` rather than treating exit code 0 as successful test evidence.
- **Subprocess Pipe Isolation & Worker Node Reuse**: Mitigated process hangs on inherited standard output/error pipes by disabling MSBuild worker node reuse (`MSBUILDDISABLENODEREUSE=1`) and bounding stream reader draining upon process exit.
- **Verification Coverage Semantics**: Scope resolution distinguishes between declared task scope and entire-repository coverage. Unrelated out-of-scope unsupported workspaces (e.g., Flutter or Python) remain visible in `unsupportedWorkspaces` without invalidating scoped verification, while repository-wide tasks explicitly emit `UNSUPPORTED_WORKSPACE_VERIFIER` and `MISSING_WORKSPACE_EVIDENCE_PROVIDER` gaps to prevent false `Verified` claims.
- **Portable MCP Integration Tests**: Replaced hardcoded `D:\project\...` paths and silent-return false-positive tests with deterministic, portable MCP stdio integration tests on temporary repositories.
- **Dogfood Test Accounting**: External dogfood suites dynamically report `Skipped` with clear reasons when repository environment variables (`AGENTPROOF_EDUCATION_REPO`, `AGENTPROOF_ASRP_REPO`) are absent, but fail explicitly when `AGENTPROOF_REQUIRE_DOGFOOD=true`.

### Changed

- **Benchmark Semantic Comparison**: Upgraded `BenchmarkComparer` from aggregate metric counts to deep semantic equivalence comparisons covering workspace IDs, paths, technologies, step commands, exit codes, failure reasons, gap codes, criteria status, and accounting consistency.
- **Benchmark Backward Compatibility**: Preserved graceful fallback with clear warnings when comparing against older scalar-only benchmark artifacts.

### Security & Documentation

- **Trust Model & Security Context**: Expanded `docs/security.md` with explicit trust boundary definitions, documenting that verification executes repository-defined MSBuild targets and npm scripts and must be run in appropriate security contexts.
- **Contract Accuracy**: Aligned `docs/compact-mcp-output.md` and `docs/mcp-integration.md` to clarify that `detailLevel: "full"` provides structured diagnostic evidence within `schemaVersion: 1` (not internal Domain entities) and documented execution implications.

### CI & Packaging

- **Cross-Platform Matrix CI**: Configured `.github/workflows/ci.yml` to run full unit and portable MCP integration suites on both Ubuntu and Windows runners.
- **Dedicated Dogfood Workflow**: Added `.github/workflows/dogfood.yml` for dispatchable validation against external dogfood repositories.
- **Preview Release Preparation**: Updated `Directory.Build.props` to `0.3.0-preview.3`.

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
