# 🛡️ Step 12.20B: `mdfetch` Documentation Whitelist & SSRF Governance

> **Feature Implementation Step (Part 2 of 2 — `IMarkdownFetchSafetyService`, `mdfetch-safety.json` & Settings Tab)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.20A: Air-Gapped Mode & Web Search Deactivation](../archive/phase-12/STEP_12_20A_AIR_GAPPED_MODE_AND_WEB_SEARCH_DEACTIVATION.md)  
> *Related Steps:* [Step 12.14: Global Skills Injection & Stack Tooling](../archive/phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Under [Step 12.14](../archive/phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md), developers and agents use `mdfetch` (`Pmad.MarkdownFetch`) to fetch documentation directly into markdown format. In enterprise and air-gapped environments, teams want to allow fetching from internal documentation servers (e.g. `https://docs.corp.internal/*`, `http://wiki.lan/*`) while preventing SSRF and cloud metadata theft (`169.254.169.254`).

This step (Part 2 of Step 12.20) provides:
1. **`IMarkdownFetchSafetyService` & `~/.nightcodeforge/mdfetch-safety.json`:** Manage URL whitelist patterns, CIDR blocks, private IP toggles, and markdown sanitization, with hardcoded deny rules for cloud metadata (`http://169.254.169.254/*`) and non-HTTP schemes.
2. **CLI & Container Integration:** Automatically pass `--safety-config ~/.nightcodeforge/mdfetch-safety.json` (mounted read-only in container sandboxes) when executing `mdfetch`.
3. **In-App Settings UI (`MdfetchSecuritySettingsTab.razor`):** Provide a `/settings` tab to manage allowed URL patterns, test URLs interactively, and toggle safety options while keeping all validation logic in `IMarkdownFetchSafetyService` per [AGENTS.md](../../AGENTS.md).

---

## 2. Technical Specification

### 2.1 Safety Configuration Contract (`IMarkdownFetchSafetyService`)

```csharp
namespace NightCodeForge.Core.Services.Security;

public record MarkdownFetchSafetyConfig
{
    public bool EnforceWhitelist { get; init; } = true;
    public List<string> AllowedUrlPatterns { get; init; } = [];
    public List<string> BlockedCidrRanges { get; init; } = ["169.254.169.254/32", "127.0.0.0/8"];
    public bool AllowPrivateIpAccess { get; init; } = false;
    public bool SanitizeMarkdown { get; init; } = true;
    public long MaxContentLengthBytes { get; init; } = 2 * 1024 * 1024; // 2 MB default
}

public interface IMarkdownFetchSafetyService
{
    Task<MarkdownFetchSafetyConfig> GetConfigAsync(CancellationToken ct = default);
    Task SaveConfigAsync(MarkdownFetchSafetyConfig config, CancellationToken ct = default);
    Task<bool> IsUrlAllowedAsync(string url, CancellationToken ct = default);
    string GetSafetyConfigFilepath();
}
```

### 2.2 Whitelist Pattern Matching & Hardcoded Deny Rules
- Supported wildcard patterns: `https://docs.corp.internal/*`, `https://*.internal-domain.com/*`, `http://localhost:8080/api/*`.
- **Hardcoded Deny Rules:**
  - Cloud metadata (`http://169.254.169.254/*`) is **always denied**, even if a wildcard pattern like `http://*` is configured.
  - Non-HTTP schemes (`file://`, `gopher://`, `ftp://`) are rejected.
- Atomic persistence writes `~/.nightcodeforge/mdfetch-safety.json` and mounts it read-only into `/etc/nightcodeforge/mdfetch-safety.json` for container sandboxes.

### 2.3 In-App Settings UI (`MdfetchSecuritySettingsTab.razor`)
- **Allowed Patterns List:** Table of current URL patterns with "Add Pattern" and "Remove" actions.
- **Interactive "Test URL" Bar:** Enter any URL to see an instant badge (`Allowed` green / `Blocked by Policy` red with reason).
- **Safety Toggles:** "Block Cloud Metadata (169.254.169.254)" (checked and locked), "Sanitize Markdown", and "Max Content Size (KB)".

---

## 3. Definition of Done (DoD) Checklist

- [ ] Implement `IMarkdownFetchSafetyService` and `MarkdownFetchSafetyService` in `NightCodeForge.Core` with wildcard URL matching, mandatory `169.254.169.254` blocking, and atomic `~/.nightcodeforge/mdfetch-safety.json` writing.
- [ ] Inject `--safety-config` into `mdfetch` CLI/container execution builders.
- [ ] Build `MdfetchSecuritySettingsTab.razor` in `NightCodeForge.Web` adhering to presentation separation.
- [ ] Add unit tests for `MarkdownFetchSafetyService` (whitelist matching, cloud metadata hard-deny, JSON persistence) and bUnit tests for `MdfetchSecuritySettingsTab.razor`.

