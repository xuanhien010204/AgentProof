# Compact MCP Output Design

## Scope

This design changes only the MCP adapter response representation. Repository discovery, skill recommendation, verification planning, execution, and evidence evaluation remain unchanged.

## Baseline observations

- Repository analysis repeats the request root path and workspace relative path even when the workspace ID already is that path.
- Skill recommendations repeat static catalog name and purpose data for every recommendation.
- Verification plans repeat absolute working directories, human-readable step names, and timeout values beside stable step and workspace IDs.
- Verification results repeat the complete planned step for every evidence item and repeat the same `NotRun` reason for every skipped step.

## Field classification

| Classification | Representation |
| --- | --- |
| A. Preserve directly | Workspace IDs, technologies/frameworks, package/build entry points, recommendation ID/level/reason, step IDs/types/workspaces/commands, capability gaps, status, all verification counts, failed/timed-out IDs, failure reasons, and diagnostic output. |
| B. Represent compactly | Working directories become workspace-relative paths only when IDs are insufficient; commands become one exact command string; verification evidence becomes status groups. |
| C. Aggregate | Verification accounting is emitted once; passed IDs and identical NotRun reasons are grouped instead of repeating an entire evidence object per step. |
| D. Omit when redundant | Repository root path is request input; a workspace relative path is omitted only when it equals its stable ID; static skill catalog name/purpose is represented by the stable skill ID; per-step duration is omitted because benchmark latency supplies timing and durations are not diagnostic evidence. |

## Compact response rules

- `analyze_repository` retains repository facts, workspace count, each workspace ID, and local technology/framework/build metadata. Workspaces without a supported .NET or Node verifier explicitly carry an unsupported-verifier reason.
- `recommend_skills` retains the stable skill ID, recommendation level, reason code, and reason.
- `create_verification_plan` retains the planned count, workspace IDs, exact command intent, type, provided evidence, timeout, required flag, workspace association, and capability gaps. The absolute working directory is only emitted when its relative path cannot be inferred from the workspace ID.
- `verify` retains final status, one complete accounting summary, passed IDs, failed/timed-out diagnostic evidence, grouped NotRun IDs and reason, criteria, gaps, and unsupported verifier state.

The response records use `JsonIgnore` for absent optional fields only; a missing optional field has a documented deterministic meaning and is never used to hide a failure, gap, or workspace.

## Contract Hardening and Versioning

1. **Explicit Schema Version**:
   - All root MCP responses (`CompactRepositoryProfile`, `CompactVerificationPlan`, `CompactVerificationResult`) and `CompactSkillRecommendation` provide an explicit `schemaVersion: 1`.
   - `schemaVersion` is ordered first in JSON object output to ensure deterministic, self-describing payloads for consuming agents.

2. **Progressive Disclosure (`detailLevel`)**:
   - `verify` supports an optional `detailLevel` argument:
     - `"compact"` (default): Returns the context-efficient summary (750 bytes on EducationCMS, ~1.3KB on ASRP) with zero successful output and durations omitted. Failed step identities, exit codes, and diagnostic output summaries are always preserved directly in `failedSteps`.
     - `"full"`: Retains the exact same verification semantics, summary, status, and failures, but additionally populates an `evidence` array containing `DetailedVerificationEvidence` for every step (including passing step stdout and per-step duration in milliseconds).
   - Verification execution is never altered by requesting detail; evidence is mapped from the identical execution result.

3. **Compatibility Assessment**:
   - The Compact MCP wire format is a **breaking contract change** for strict consumers expecting the internal domain model shape (e.g. `evidence` array with embedded `step` objects in `verify`, or nested `skill` objects in `recommend_skills`).
   - Legacy consumers requiring per-step evidence can request `detailLevel = "full"` to retrieve explicit evidence objects.

