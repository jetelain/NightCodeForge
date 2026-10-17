# 🗑️ Step 9.4: Remove Non-Functional A/B Testing & Dead Navigation Relics

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Apply the Clean Break policy to remove non-functional features. The A/B testing comparator tab (`ABTestingComparatorTab.razor`) was an early proof-of-concept that does not work (it queries `StageExecutionRecords` using stage IDs as profile IDs and lacks execution engine support). Completely remove this component along with vestigial navigation labels ("Settings & A/B", "temperature tournaments") and dead dependency injection parameters across Web services. We may reintroduce a proper empirical evaluation harness in a later version.

---

## 2. Technical Scope & Specifications

### 2.1 Delete Non-Functional A/B Testing Component
* **Target File:** `src/NightCodeForge.Web/Components/Settings/ABTestingComparatorTab.razor`
* **Remediation:**
  * Delete the file `src/NightCodeForge.Web/Components/Settings/ABTestingComparatorTab.razor`.

### 2.2 Prune A/B Testing Tab from Settings Page
* **Target File:** `src/NightCodeForge.Web/Components/Pages/Settings.razor`
* **Remediation:**
  * Remove the "📊 A/B Testing Comparator" navigation button from the global settings tab bar (lines 73–78).
  * Remove `case "abtesting": <ABTestingComparatorTab /> break;` from the `@switch (_activeGlobalTab)` block (lines 105–107).
  * Ensure the default active tab remains `"optimizations"` or another valid tab.

### 2.3 Rebrand Navigation Menus & Remove Vestigial References
* **Target Files:**
  * `src/NightCodeForge.Web/Components/Layout/NavMenu.razor`
  * `src/NightCodeForge.Web/Components/Layout/TopNavBar.razor`
  * `src/NightCodeForge.Web/Components/Pages/Home.razor`
* **Remediation:**
  * In `NavMenu.razor`: change `<span>Settings &amp; A/B</span>` to `<span>Settings</span>`.
  * In `TopNavBar.razor`: change `"settings" => "Settings & A/B"` to `"settings" => "Settings"`.
  * In `Home.razor`: update the quick link card title from "Settings & A/B" to "Settings" and change description from *"Configure LLM inference models, temperature tournaments, and host SDKs."* to *"Configure LLM inference models, agent profiles, workflows, and host SDKs."*

### 2.4 Prune Dead Injected Dependency in `WorkspaceProvisioningService`
* **Target File:** `src/NightCodeForge.Web/Services/WorkspaceProvisioningService.cs`
* **Context:**
  The constructor accepts `AppDbContext? dbContext = null` and assigns `_dbContext = dbContext;`, but `_dbContext` is never referenced anywhere in the service.
* **Remediation:**
  * Remove `AppDbContext? dbContext = null` and the unused private field `_dbContext`.

---

## 3. Verification & Testing

* Execute `dotnet build` to confirm zero compilation errors.
* Execute `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
* Verify that the Settings page renders with the remaining 6 global tabs: Global Optimizations, Agent Profiles, Workflows & Stages, Host SDKs, OllamaRouter Gateway, and Cloud & Web Search.
