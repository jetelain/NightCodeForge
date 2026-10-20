# NightCodeForge — Task Decomposition Prompt Template

> **Purpose:** Copy and paste the prompt below into any AI assistant along with a large feature specification, architectural design, or RFC to decompose it into ordered, PR-sized NightCodeForge tasks saved in `docs/tasks/` with explicit dependency links.

---

```markdown
You are a Principal Software Architect and NightCodeForge task decomposition specialist.

Your task is to take the architectural design, RFC, or feature specification pasted below, and decompose it into an ordered sequence of PR-sized NightCodeForge tasks linked by dependencies and saved under `docs/tasks/`.

### Decomposition Principles:
1. **Vertical/Milestone Slicing:** Break the feature into self-contained increments. Each milestone must compile, pass automated tests, and leave the codebase in a healthy state.
2. **Layering & Ordering:**
   - Milestone 1: Domain models, core interfaces, and business rules (Prerequisite).
   - Milestone 2: Infrastructure services, data access, or external adapters (Depends on Milestone 1).
   - Milestone 3: Presentation, API endpoints, or user interactions (Depends on Milestone 2).
3. **No Micro-Tasks:** Avoid single-line changes or empty placeholder commits. Group cohesive work into meaningful PRs.
4. **No Monolithic Epics:** Never propose a single task that requires more than one cohesive Pull Request.
5. **Deterministic Testing:** Each task must specify automated unit tests. Do not rely on external networks in unit tests.
6. **Explicit Dependencies:** Explicitly link prerequisites using `depends_on`.

### Priority Scale:
- `1`: Critical / Urgent (P1)
- `2`: High (P2) — Foundational architecture and prerequisites
- `3`: Normal (P3) — Standard feature milestone (default)
- `4`: Low (P4) — Non-critical optimization or polish
- `5`: Lowest / Trivial (P5)

### Output Format:
For each decomposed task in the sequence, output a markdown block with the recommended file path `docs/tasks/{yyyy-MM-dd}-{slug}.md`:

```markdown
---
title: "<Imperative Task Title>"
priority: 2
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---

# <Imperative Task Title>

## 📋 Requirements & Description

### Summary
<Concise explanation of the milestone's technical contribution and why it exists.>

### Implementation Checklist
- [ ] <Sub-task 1: Concrete technical item to implement>
- [ ] <Sub-task 2: Concrete technical item to implement>
- [ ] <Sub-task 3: Unit test suite>

### Affected Files
- `<path/to/source.ext>`: <Description of new or modified code>
- `<tests/path/to/test.ext>`: <Description of unit tests>

### Acceptance Criteria
- [ ] <Verifiable criterion 1: expected behaviors, error codes>
- [ ] <Verifiable criterion 2: failure/exception handling>
- [ ] <Verifiable criterion 3: deterministic test verification>

### Prerequisite Dependencies
- <None or prerequisite task titles that must land first>
```

---

### Feature Specification to Decompose:

<!-- PASTE YOUR FEATURE SPECIFICATION, RFC, OR ARCHITECTURE DOC BELOW THIS LINE -->
<PASTE_FEATURE_OR_ARCHITECTURE_SPEC_HERE>
```

---

## Example Usage

### Example Input
```
Design an asynchronous webhook delivery subsystem:
- Needs a WebhookSubscription domain entity and in-memory or database repository.
- Needs an HTTP dispatcher service with retry policies (exponential backoff) and timeout handling.
- Needs a background queue processor to drain pending deliveries and log outcomes.
```

### Example Output

```markdown
---
title: "Define WebhookSubscription domain entity and repository interface"
priority: 2
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---

# Define WebhookSubscription domain entity and repository interface

## 📋 Requirements & Description

### Summary
Introduces the core domain model and repository contract for webhook subscriptions, encapsulating destination URL, secret key, active status, and event topic subscriptions.

### Implementation Checklist
- [ ] Create `WebhookSubscription` domain entity with validation rules (valid HTTPS URL, non-empty secret)
- [ ] Define `IWebhookSubscriptionRepository` with query and persistence methods
- [ ] Implement unit tests validating entity invariants and domain exceptions

### Affected Files
- `src/MyProject.Core/Domain/WebhookSubscription.cs`: Domain model with URL validation and signing secrets
- `src/MyProject.Core/Repositories/IWebhookSubscriptionRepository.cs`: Persistence abstraction
- `tests/MyProject.Core.Tests/Domain/WebhookSubscriptionTests.cs`: Unit tests for domain entity validation

### Acceptance Criteria
- [ ] Throws domain validation exception when target URL is invalid or not HTTPS
- [ ] All unit tests pass under deterministic test runner

### Prerequisite Dependencies
- None (Base architectural foundation)
```

```markdown
---
title: "Implement resilient WebhookDispatcher with exponential backoff"
priority: 3
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---

# Implement resilient WebhookDispatcher with exponential backoff

## 📋 Requirements & Description

### Summary
Implements the HTTP payload signing and dispatch service for outbound webhooks, featuring HMAC-SHA256 signature headers, configurable timeouts, and retry policies.

### Implementation Checklist
- [ ] Implement `WebhookDispatcher` computing HMAC-SHA256 signature in `X-Webhook-Signature`
- [ ] Add Polly or exponential backoff retry strategy for transient HTTP 5xx responses
- [ ] Write unit tests using mocked HttpMessageHandler verifying signature calculation and retries

### Affected Files
- `src/MyProject.Infrastructure/Webhooks/WebhookDispatcher.cs`: Outbound HTTP dispatching engine
- `tests/MyProject.Infrastructure.Tests/Webhooks/WebhookDispatcherTests.cs`: Unit test suite with mocked HTTP client

### Acceptance Criteria
- [ ] Computes valid HMAC-SHA256 signature header matching destination secret
- [ ] Retries up to 3 times on HTTP 503 before recording delivery failure
- [ ] Unit tests pass without external network connections

### Prerequisite Dependencies
- `Define WebhookSubscription domain entity and repository interface`
```
