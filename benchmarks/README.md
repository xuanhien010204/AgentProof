# AgentProof Context Efficiency Benchmark Harness

Deterministic context efficiency and assurance benchmark harness for AgentProof.

## Purpose

AgentProof is a local-first engineering assurance layer for AI coding agents (such as Antigravity and OpenAI Codex).

Host AI models currently do not expose reliable total token consumption directly to MCP servers. Instead of guessing or fabricating token numbers, this benchmark harness deterministically measures the parts AgentProof observes accurately:

- **MCP Response Payload Size** (exact UTF-8 serialized byte count)
- **Response Character Count**
- **Execution Duration & Latency** (min, median, max over N iterations)
- **Structural Assurance Metrics**:
  - Workspace count
  - Verification step count
  - Evidence count
  - Gap count
  - Final verification status

> **Important**: Payload reduction is a context-efficiency proxy, not a measurement of total LLM token usage.

---

## What This Harness Measures vs What It Does Not

### Measured
- AgentProof-controlled MCP tool response payload size (UTF-8 bytes)
- JSON-RPC stdio transport wire payload size
- Real execution duration (min, median, max across iterations)
- Verification correctness invariants (verification status, evidence preserved, gaps prevented)

### NOT Measured
- Total Codex tokens
- Total Antigravity tokens
- Hidden host system prompts
- Model reasoning / chain-of-thought tokens
- Host-side context window compression or caching
- LLM provider billing tokens

---

## Intended Optimization Workflow

This benchmark establishes the ground-truth baseline (starting at `0.3.0-preview.2`) before implementing future Compact MCP Output optimizations.

```
0.3.0-preview.2 Baseline
           ↓
    record baseline
           ↓
 implement Compact MCP
           ↓
    record candidate
           ↓
        compare
           ↓
verify payload reduction
without correctness regression
```

---

## Supported Scenarios

| Scenario | Operations Executed | Description |
|---|---|---|
| `AnalyzeOnly` | `analyze_repository` | Measures repository structure and workspace detection payload. |
| `Planning` | `analyze_repository`, `create_verification_plan` | Measures planning context size. |
| `FullVerification` | `analyze_repository`, `recommend_skills`, `create_verification_plan`, `verify` | End-to-end assurance cycle with tool execution. |

---

## Usage

### 1. Run Benchmark against a Repository

```bash
# Full verification benchmark with 3 iterations (default)
dotnet run --project benchmarks/AgentProof.Benchmarks -- \
  --repository "D:\project\EducationCMS" \
  --scenario FullVerification \
  --output artifacts/benchmarks/educationcms-baseline.json \
  --markdown artifacts/benchmarks/educationcms-baseline.md

# Planning scenario with custom task context
dotnet run --project benchmarks/AgentProof.Benchmarks -- \
  --repository "D:\project\ASRP" \
  --scenario Planning \
  --task-context benchmarks/tasks/asrp-fullstack.json \
  --output artifacts/benchmarks/asrp-planning-baseline.json
```

### 2. Compare Candidate Against Baseline

```bash
dotnet run --project benchmarks/AgentProof.Benchmarks -- compare \
  --baseline artifacts/benchmarks/asrp-baseline.json \
  --candidate artifacts/benchmarks/asrp-compact.json \
  --output artifacts/benchmarks/asrp-comparison.json \
  --markdown artifacts/benchmarks/asrp-comparison.md
```

---

## Correctness Guards & Invariants

A candidate optimization is **ONLY** marked `PASS` if payload is reduced **WITHOUT** degrading assurance correctness:

| Verdict | Trigger Conditions |
|---|---|
| `PASS` | Candidate payload smaller or equal, verification status unchanged, evidence count unchanged, no new gaps. |
| `WARNING` | Workspace count changed unexpectedly, or minor non-fatal variance. |
| `FAIL` | Verification status regressed (e.g. `Verified` &rarr; `NotVerified`), evidence missing, new gaps introduced, or candidate payload increased. |
