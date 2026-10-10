# Security Policy & Trust Model

AgentProof is designed with local-first, defense-in-depth principles. It executes entirely on the local machine or host container. It does not upload source code, make outbound LLM API requests, require cloud API keys, or transmit telemetry.

---

## 1. Trust Model & Execution Context

### Repository Trust Boundary
AgentProof enforces rigorous controls over **what commands are formed**, but it **does not isolate or sandbox the operating system process** executing those commands.

When the `verify` tool executes:
- It invokes approved developer toolchains (`dotnet`, `npm`, `pnpm`, `yarn`, `npx`).
- Those toolchains execute code and targets defined by the analyzed repository itself—including MSBuild custom tasks, NuGet package targets, `package.json` lifecycle scripts, and test suite code.
- Consequently, code authored in the repository runs with the permissions of the user account executing AgentProof.

### Operating Security Context
> [!IMPORTANT]
> AgentProof must always be executed within a security context appropriate for the trust level of the target repository.
> 
> When analyzing or verifying untrusted or unreviewed third-party repositories, AgentProof should be executed inside an isolated environment (such as a container, sandboxed virtual machine, or disposable CI runner) with restricted network access and isolated credentials.

---

## 2. Analysis & Planning Safety (Read-Only)

All preliminary MCP tools are strictly read-only:
- `analyze_repository`
- `recommend_skills`
- `create_verification_plan`

Security safeguards during analysis:
- **No Arbitrary Shell**: No capability exists in AgentProof to invoke user-defined or host-supplied shell commands.
- **Path Normalization**: Target repository paths are normalized with `Path.GetFullPath` and verified to exist before enumeration. Traversal attempts outside the root directory are rejected.
- **Directory Pruning**: Heavy, generated, and metadata directories (`.git`, `bin`, `obj`, `node_modules`, `.next`, `dist`, `build`, `coverage`, `TestResults`) are pruned from deep traversal.
- **Configuration-Only Inspection**: Only small, recognized project files (`*.csproj`, `*.sln`, `*.slnx`, `package.json`) are parsed. Binary files and arbitrary source files are never loaded or executed during analysis.

---

## 3. Verification Execution Defenses

The `verify` tool executes verification steps subject to strict defensive controls:

1. **Internally Synthesized Plans**:
   - `verify` synthesizes verification steps internally from inspected repository artifacts and the structured `TaskContext`.
   - Callers cannot pass arbitrary command lines, binary paths, or flags to `verify`.

2. **Strict Command Allowlist**:
   - Only approved executables are permitted: `dotnet`, `npm`, `pnpm`, `yarn`, `npx`.
   - For `dotnet`: Only verbs `restore`, `build`, and `test` targeting verified `.sln`, `.slnx`, or `.csproj` files located inside the repository root.
   - For Node: Only approved script names (`lint`, `typecheck`, `test`, `build`) declared in `package.json`.
   - For Playwright: Only the fixed invocation `--no-install playwright test`.

3. **No Shell Wrapping (`UseShellExecute = false`)**:
   - AgentProof launches processes directly using `ProcessStartInfo` with `UseShellExecute = false`.
   - Commands are never passed to shell interpreters (`cmd.exe /c`, `powershell.exe`, or `sh -c`), preventing command injection and shell metacharacter manipulation.
   - Arguments are supplied via `ProcessStartInfo.ArgumentList`, guaranteeing OS-level escaping.

4. **Repository Root Containment**:
   - Every working directory and project target is validated against the canonical repository root via `Path.GetFullPath`.
   - If a target or working directory escapes the repository root (`../`), execution is immediately aborted with `InvalidOperationException`.

5. **Process Stdin Isolation**:
   - Immediately upon starting any verification process, `process.StandardInput.Close()` is called.
   - This ensures verification processes cannot hang waiting for interactive input, confirmations, or credential prompts.

6. **Fail-Fast & Process Tree Termination**:
   - Every step enforces an explicit timeout (e.g. 60–120 seconds).
   - If a process times out or caller cancellation is triggered, AgentProof terminates the entire process tree (`process.Kill(entireProcessTree: true)`) before returning, preventing orphaned background worker processes.
   - If a required step fails, subsequent dependent steps are cancelled immediately with full `NotRun` step accounting.

7. **Stream Limiting & Secret Redaction**:
   - Standard output and standard error are captured with a hard limit of 64 KiB per stream to protect host context windows.
   - Pattern matching redacts common credentials, tokens, passwords, and secret keys in output with `[REDACTED]`.
   - Host environment variables are never dumped or returned over the MCP connection.

---

## 4. Verification Status Semantics

The evaluation engine assigns unambiguous status:
- **`Verified`**: All required checks in declared scope passed with exit code `0`, and no verification gaps exist.
- **`NotVerified`**: One or more required checks failed or timed out.
- **`PartiallyVerified`**: Executed checks passed, but explicit evidence gaps remain (such as missing required evidence providers or repository-wide verification encountering unsupported workspaces). Unsupported out-of-scope workspaces do not invalidate scoped verification.
