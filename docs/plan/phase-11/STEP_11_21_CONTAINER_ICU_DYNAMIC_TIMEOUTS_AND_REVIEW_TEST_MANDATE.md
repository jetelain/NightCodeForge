# 🛡️ Step 11.21: Dynamic Tool Timeouts, Container ICU & User Permissions, and Review Rejection Test Mandate

> **Phase 11 Implementation Step (Post-MVP - Toolchain Robustness & Quality Gates)**  
> *Parent Plan:* [Phase 11 Master Plan](../11_EXTEND_FEATURES.md)  
> *Project Target:* `src/NightCodeForge.Agents/Tools`, `src/NightCodeForge.Infrastructure/Execution`, `src/NightCodeForge.Core/Domain`, `src/NightCodeForge.Agents/Crucible`, `src/NightCodeForge.Core/Workflow`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

---

## 1. Executive Summary & Problem Statement

Real-world test passes across production repositories (**OllamaRouter** and **ComicGenCLI**) revealed four operational and architectural constraints:

1. **Premature Bash Tool Timeouts on Combined Build & Test Suites:**
   Developers encountered command terminations:
   `[TIMEOUT]: Command execution timed out after 180s. Execution was aborted to prevent resource lockup. Check for hanging tests, infinite loops, or deadlocks.`
   *Root Cause:* In `CanonicalTools.cs`, the bash tool resolved timeout using individual thresholds (`_testTimeoutSeconds ?? 120` or `_buildTimeoutSeconds ?? 120` or `DefaultCommandTimeoutSeconds = 60`). When agents run combined commands (`dotnet build && dotnet test`) or when repositories have comprehensive suites, the 180s ceiling was prematurely triggered.
   *Resolution:* Enforce a dynamic timeout formula for the bash tool:
   $$\text{Timeout} = \max(300\text{s (5 mins)}, \text{BuildTimeout} + \text{TestTimeout})$$
   ensuring operations have sufficient runway while still preventing runaway infinite loops.

2. **Missing `libicu` and Invariant Globalization in Agent Container:**
   During .NET builds in the Docker sandbox, builds produced warnings:
   `/usr/lib/dotnet/sdk/10.0.112/Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets(266,5): warning NETSDK1188: Package Microsoft.TestPlatform.ObjectModel 18.10.1 has a resource with the locale 'fr'. This locale is not recognized by .NET. Consider notifying the package author that it appears to be using an invalid locale.`
   *Root Cause:* The base container image (`DefaultAgentBaseDockerfile`) lacked `libicu` libraries and locale generation packages. Furthermore, `NightCodeForgeContainerExecutor` explicitly injected `export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. In invariant mode, .NET cannot recognize international resource assemblies (e.g. `'fr'`, `'de'`, `'es'`).
   *Resolution:* Update `DefaultAgentBaseDockerfile` to install `libicu-dev` (or `libicu74`), `locales`, and generate UTF-8 locales (`en_US.UTF-8`, `fr_FR.UTF-8`). Configure `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0`, `LANG=en_US.UTF-8`, and `LC_ALL=en_US.UTF-8`, giving .NET full internationalization support.

3. **Container File Permission Conflicts & Root File Ownership:**
   When commands, builds, or code generation runs inside `NightCodeForgeContainerExecutor` (`docker run` / `docker exec`), Docker defaults to execution as `root` (UID 0, GID 0) unless an explicit user flag is provided. When the container generates or edits files in the bind-mounted workspace or repository clone (e.g. `obj/`, `bin/`, source files, git commits, node_modules), these files become owned by `root:root` with restricted permissions. Subsequently, the host developer or WSL user (e.g. UID 1000, GID 1000) encounters `EACCES: permission denied` or `UnauthorizedAccessException` when running host tools, git commands, or cleaning up workspaces.
   *Resolution:* Maintain the host/WSL user name and group inside the container execution lifecycle. Start and use the container with `-u $(id -u):$(id -g)` (e.g., `--user <uid>:<gid>`). Capture the host/WSL UID and GID once upon environment initialization (e.g., querying `id -u` and `id -g` via the process runner and caching the result) to avoid repeatedly spawning subshells. Pass `--user <uid>:<gid>` to both `docker run` in `BuildDockerRunArgs` and `docker exec` in `BuildProcessStartSpec`. Configure container environment paths (`HOME=/tmp/ncf-home` or user home) so toolchains (git, dotnet, npm) have a writable user configuration directory.

4. **Missing Automated Test Mandate on Review Agent Rejection:**
   When the Stage 3 Review Agent rejected an implementation via `request_changes`, it listed missing features or code bugs, but did not strictly require the implementation agent to create automated tests for each defect found. Consequently, the Stage 2 Implementation Agent often applied quick patches without writing regression tests, leading to repeat rejections.
   *Resolution:* Update review directives in `StandardWorkflowDefinitions` and `CrucibleAgentHarness`: when rejecting an implementation, the Review Agent MUST instruct the implementation agent to author a dedicated automated test (unit or integration) for every bug found. Update the Implementation Agent prompt to enforce this test creation requirement upon loopback.

---

## 2. Architecture & Data Flow

```mermaid
flowchart TD
    subgraph TimeoutCalculation ["Dynamic Bash Timeout Engine"]
        Config["Workspace Project Config<br/>(BuildTimeout: 120s, TestTimeout: 180s)"] --> Formula["Math.Max(300, BuildTimeout + TestTimeout)"]
        Formula --> EffectiveTimeout["Effective Timeout = 300s (5m)"]
        EffectiveTimeout --> BashTool["CanonicalTools.bash(command, timeout)"]
    end

    subgraph ContainerGlobalization ["Full ICU Container Environment"]
        Dockerfile["Ubuntu 24.04 Base Image"] --> Apt["Install libicu-dev + locales"]
        Apt --> LocaleGen["locale-gen en_US.UTF-8 fr_FR.UTF-8"]
        LocaleGen --> EnvVars["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0<br/>LANG=en_US.UTF-8<br/>LC_ALL=en_US.UTF-8"]
        EnvVars --> DotNetSDK["DotNet SDK Package Resolution<br/>(Zero NETSDK1188 Locale Warnings)"]
    end

    subgraph ContainerUserPermissions ["Container Host User & Group ID Preservation"]
        HostEnv["Host / WSL Environment"] --> CaptureId["Capture UID & GID Once<br/>(id -u & id -g)"]
        CaptureId --> CachedOptions["Cached UserUidGid in ContainerExecutorOptions<br/>(e.g. '1000:1000')"]
        CachedOptions --> DockerRun["docker run --user &lt;uid&gt;:&lt;gid&gt; ..."]
        CachedOptions --> DockerExec["docker exec --user &lt;uid&gt;:&lt;gid&gt; ..."]
        DockerRun --> WorkspaceFiles["Files in Bind-Mounted /workspace<br/>Owned by Host User (Zero EACCES Conflicts)"]
        DockerExec --> WorkspaceFiles
    end

    subgraph ReviewLoopbackEnforcement ["Review Rejection Test Mandate"]
        Reviewer["Stage 3: Review Agent"] --> DefectsFound{"Defects / Bugs Detected?"}
        DefectsFound -->|Yes| RequestChanges["Invoke 'request_changes' Tool"]
        RequestChanges --> Directive["Mandate: Add Automated Regression Test<br/>for Each Defect Identified"]
        Directive --> Loopback["Loopback to Stage 2: Implementation"]
        Loopback --> ImplAgent["Implementation Agent Writes Tests First,<br/>Verifies Green Suite, Passes Quality Gate"]
    end
```

---

## 3. Detailed Specifications

### 3.1 Dynamic Bash Tool Timeout Formula

1. **Update [CanonicalTools.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalTools.cs):**
   - Replace the legacy timeout branching in `bash`:
     ```csharp
     // Minimum ceiling of 5 minutes (300 seconds)
     const int MinimumBashTimeoutSeconds = 300;

     int configuredBuildTimeout = _buildTimeoutSeconds ?? WorkflowDefaults.DefaultBuildTimeoutSeconds;
     int configuredTestTimeout = _testTimeoutSeconds ?? WorkflowDefaults.DefaultTestTimeoutSeconds;
     int combinedSuiteTimeout = Math.Max(MinimumBashTimeoutSeconds, configuredBuildTimeout + configuredTestTimeout);

     int effectiveTimeout;
     if (timeout.HasValue && timeout.Value > 0)
     {
         // If agent requests an explicit timeout, honor it up to the combined suite ceiling or specified value
         effectiveTimeout = Math.Max(timeout.Value, combinedSuiteTimeout);
     }
     else
     {
         effectiveTimeout = combinedSuiteTimeout;
     }
     ```
   - This ensures simple commands receive ample time, while combined build and test invocations never time out prematurely at 180s.

2. **Update [WorkflowDefaults.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/WorkflowDefaults.cs):**
   - Define canonical constant:
     ```csharp
     public const int MinimumBashToolTimeoutSeconds = 300;
     ```

### 3.2 Full ICU Globalization in Agent Base Container

1. **Update [ContainerExecutorOptions.DefaultAgentBaseDockerfile](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs):**
   - Add `libicu-dev`, `locales` to `apt-get install`.
   - Run `locale-gen en_US.UTF-8 fr_FR.UTF-8` and configure locale environment variables:
     ```dockerfile
     FROM ubuntu:24.04
     ENV DEBIAN_FRONTEND=noninteractive
     RUN apt-get update && apt-get install -y --no-install-recommends \
         git \
         ripgrep \
         curl \
         ca-certificates \
         jq \
         procps \
         build-essential \
         unzip \
         tar \
         gzip \
         python3 \
         python3-venv \
         python3-pip \
         locales \
         libicu-dev \
         && locale-gen en_US.UTF-8 fr_FR.UTF-8 \
         && rm -rf /var/lib/apt/lists/*

     ENV LANG=en_US.UTF-8
     ENV LANGUAGE=en_US:en
     ENV LC_ALL=en_US.UTF-8
     ```

2. **Update [NightCodeForgeContainerExecutor.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs):**
   - In `ExecuteAsync`:
     - Replace:
       ```csharp
       await _stdin!.WriteLineAsync("export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1");
       ```
       with:
       ```csharp
       await _stdin!.WriteLineAsync("export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0");
       await _stdin!.WriteLineAsync("export LANG=en_US.UTF-8");
       await _stdin!.WriteLineAsync("export LC_ALL=en_US.UTF-8");
       ```
   - In `EnsureImageReady`:
     - Update base image tag or version hash (e.g. `ncf-agent-base:v2`) so that existing local images missing `libicu` are automatically upgraded.

### 3.3 Host User & Group ID Preservation in Container Execution (`-u $(id -u):$(id -g)`)

To eliminate file permission conflicts where files generated inside the container become owned by `root:root`, enforce matching host user UID and GID across all container operations:

1. **Option Property & User ID Resolution (`ContainerExecutorOptions.cs` & `HostUserIdentityResolver.cs`):**
   - Add configuration property:
     ```csharp
     /// <summary>
     /// Target container user identity formatted as 'uid:gid' (e.g. '1000:1000').
     /// When null or empty, NightCodeForgeContainerExecutor discovers and captures the host/WSL identity once.
     /// </summary>
     public string? UserUidGid { get; set; }
     ```
   - Implement `IHostUserIdentityResolver` / discovery helper:
     - When `UseWsl = true` or running on Linux, query user identity once via:
       `id -u` and `id -g` (e.g. using `IProcessRunner.Run(new ProcessStartSpec { FileName = "wsl.exe", ArgumentList = ["--", "sh", "-c", "echo $(id -u):$(id -g)"] })`).
     - Cache the resolved string (e.g. `"1000:1000"`) in `ContainerExecutorOptions.UserUidGid` so subshells are not executed on every command.
     - Fallback: default to `"1000:1000"` (the standard primary user UID/GID on Ubuntu/Debian/WSL) if command fails or on native Windows without WSL.

2. **Apply `--user <uid>:<gid>` to `docker run` (`BuildDockerRunArgs`):**
   - In `BuildDockerRunArgs(ContainerExecutorOptions options)`:
     ```csharp
     var effectiveUser = options.IsRestrictedReadOnly
         ? "1000:1000"
         : options.UserUidGid;

     if (!string.IsNullOrWhiteSpace(effectiveUser))
     {
         args.AddRange(["--user", effectiveUser]);
     }
     ```

3. **Apply `--user <uid>:<gid>` to `docker exec` (`BuildProcessStartSpec` & `BuildProcessStartInfo`):**
   - In `BuildProcessStartSpec(ContainerExecutorOptions options)`:
     ```csharp
     var effectiveUser = options.IsRestrictedReadOnly
         ? "1000:1000"
         : options.UserUidGid;

     if (!string.IsNullOrWhiteSpace(effectiveUser))
     {
         dockerArgs.AddRange(["--user", effectiveUser]);
     }
     ```
   - Mirror the same `--user` argument in `BuildProcessStartInfo`.

4. **Writable Home Directory & Toolchain Cache Isolation:**
   - When running as an arbitrary non-root UID, tools like git, dotnet, and npm attempt to write configuration and package caches to `$HOME`.
   - In `NightCodeForgeContainerExecutor.ExecuteAsync`:
     - Configure `$HOME` to point to a universally writable directory inside the container:
       ```csharp
       await _stdin!.WriteLineAsync("export HOME=\"/tmp/ncf-home\"");
       await _stdin!.WriteLineAsync("mkdir -p /tmp/ncf-home 2>/dev/null || true");
       ```
     - Preserve safe git configuration for arbitrary ownership:
       ```csharp
       await _stdin!.WriteLineAsync("git config --global --add safe.directory '*' 2>/dev/null || true");
       ```

5. **Shared Package Cache Preservation (`NUGET_PACKAGES=/cache/nuget`):**
   - **The Problem:** In the legacy setup, `NightCodeForgeContainerExecutor.BuildVolumeMounts` mounted host caches to `/root/.nuget/packages` and `/root/.npm`. When running with `-u $(id -u):$(id -g)`, a non-root user cannot traverse `/root` (`chmod 700`). Furthermore, the .NET SDK defaults to `$HOME/.nuget/packages`, completely ignoring `/root/.nuget/packages`.
   - **Relocate Mount Destinations to Neutral Cache Paths:**
     Update `BuildVolumeMounts` in `NightCodeForgeContainerExecutor.cs` to map package caches to neutral, non-root-accessible container paths:
     - NuGet: `/cache/nuget` (mounted from `options.NugetPackageCachePath`, e.g. `~/.nuget/packages`)
     - npm: `/cache/npm` (mounted from `options.NpmCachePath`, e.g. `~/.npm`)
     - pnpm: `/cache/pnpm` (mounted from `options.PnpmCachePath`, e.g. `~/.local/share/pnpm/store`)
     - uv: `/cache/uv` (mounted from `options.UvCachePath`, e.g. `~/.cache/uv`)
     - Maven: `/cache/m2` (mounted from `options.MavenCachePath`, e.g. `~/.m2/repository`)
   - **Create Cache Mount Directories in Base Dockerfile:**
     In `ContainerExecutorOptions.DefaultAgentBaseDockerfile`:
     ```dockerfile
     RUN mkdir -p /cache/nuget /cache/npm /cache/pnpm /cache/uv /cache/m2 \
         && chmod -R 777 /cache
     ENV NUGET_PACKAGES=/cache/nuget
     ENV npm_config_cache=/cache/npm
     ENV UV_CACHE_DIR=/cache/uv
     ```
   - **Export `NUGET_PACKAGES` Environment Variable:**
     .NET and MSBuild universally honor the `NUGET_PACKAGES` environment variable for the global packages cache.
     - In `BuildDockerRunArgs`: pass `-e NUGET_PACKAGES=/cache/nuget`, `-e npm_config_cache=/cache/npm`, `-e UV_CACHE_DIR=/cache/uv`.
     - In `NightCodeForgeContainerExecutor.ExecuteAsync`:
       ```csharp
       if (_options.MountPackageCaches)
       {
           await _stdin!.WriteLineAsync("export NUGET_PACKAGES=\"/cache/nuget\"");
           await _stdin!.WriteLineAsync("export npm_config_cache=\"/cache/npm\"");
           await _stdin!.WriteLineAsync("export UV_CACHE_DIR=\"/cache/uv\"");
       }
       ```
   - **Why This Guarantees Shared Cache Functionality:**
     1. **Universal Access:** `/cache/nuget` is world-accessible, completely bypassing restricted `/root` permissions.
     2. **Zero Cache Misses:** `dotnet restore`, `dotnet build`, and `dotnet test` read directly from the host's existing package cache without re-downloading packages.
     3. **Permissions Parity:** Because container execution uses `-u $(id -u):$(id -g)`, any newly downloaded `.nupkg` written into `/cache/nuget` inherits the host user's UID and GID, preserving full read/write compatibility on the host without `root:root` ownership or `EACCES` errors.

### 3.4 Review Agent Rejection Test Mandate

1. **Update [StandardWorkflowDefinitions.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/StandardWorkflowDefinitions.cs):**
   - Enhance the Review Stage Directive (Tier 2 Rejection Gate):
     ```text
     3. TIER 2 - REJECTION GATE: Rejection is strictly reserved for major tasks or missing features. If core acceptance criteria are unfulfilled, if tests fundamentally fail, or if bugs are found, REJECT the implementation immediately:
        - If 'request_changes' tool is available, invoke 'request_changes(feedback, reason)' immediately.
        - Otherwise, conclude your final response with:
          <request_changes>
          [Detailed explanation of missing features, broken requirements, and instructions for rework]
          </request_changes>
        MANDATORY BUG REGRESSION POLICY: When rejecting due to bugs, regressions, or unfulfilled requirements, your feedback MUST explicitly require the implementation agent to create a dedicated automated test (unit or integration test) reproducing each bug found before declaring the fix complete.
     ```

2. **Update [CrucibleAgentHarness.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs):**
   - In `BuildReviewDirective()` and `BuildImplementationDirective()`:
     - Reinforce the regression test contract:
       - **Reviewer Prompt:** "For every defect you find, instruct the implementation agent to author a corresponding automated test verifying the fix."
       - **Implementation Loopback Prompt:** "The Reviewer requested changes. Notice: You are required to implement a dedicated automated test for every bug or defect identified in the reviewer's feedback before completing your implementation."

---

## 4. Verification & Testing Strategy

1. **Unit & Integration Tests:**
   - `CanonicalToolsTests`:
     - Test that `bash` with null timeout uses `Math.Max(300, buildTimeout + testTimeout)`.
     - Test that explicit timeout $\le 300$ is promoted to at least $300\text{s}$.
   - `ContainerExecutorOptionsTests`:
     - Verify Dockerfile string includes `libicu-dev` and `locale-gen`.
     - Test `BuildDockerRunArgs` contains `--user <uid>:<gid>` when `UserUidGid` is specified.
     - Test `BuildProcessStartSpec` contains `--user <uid>:<gid>` when `UserUidGid` is specified.
     - Test `UserUidGid` fallback logic and single-capture caching.
   - `ReviewPromptTests`:
     - Verify generated system prompt for Reviewer contains the mandatory bug regression policy.
     - Verify Implementation loopback prompt contains the test authoring mandate.

2. **Manual Acceptance:**
   - Run a test suite referencing `Microsoft.TestPlatform.ObjectModel` with French resources: verify zero `NETSDK1188` locale warnings.
   - Run a 4-minute build and test command through the `bash` tool: verify it completes without timing out at 180s.
   - Trigger a container run that executes `dotnet build` or creates source files: verify files on the host/WSL filesystem are owned by the current user (`1000:1000`), NOT `root:root`, and can be modified or deleted without `sudo`.
   - Trigger a review rejection in a mock workflow: observe the generated loopback message and verify the implementation agent authors regression tests.

