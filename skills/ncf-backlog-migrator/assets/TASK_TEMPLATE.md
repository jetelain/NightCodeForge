---
title: "Implement Ollama model health check endpoint"
priority: 3
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---

# Implement Ollama model health check endpoint

## 📋 Requirements & Description

### Summary
Provide an endpoint that verifies connectivity and latency of the configured Ollama models.
When an Ollama host is unreachable or unhealthy, the endpoint returns a structured health status
allowing upstream consumers to route requests away from failing instances.

### Implementation Checklist
- [ ] Create `ModelHealthCheck` service implementing `IHealthCheck`
- [ ] Add timeout and retry logic for unresponsive Ollama instances
- [ ] Register health check in dependency injection container
- [ ] Add unit tests verifying healthy, degraded, and unreachable response scenarios

### Affected Files
- `src/OllamaRouter/Health/ModelHealthCheck.cs`: Health check implementation checking model connectivity
- `tests/OllamaRouter.Tests/ModelHealthCheckTests.cs`: Unit test suite covering success, timeout, and failure cases

### Acceptance Criteria
- [ ] Returns HTTP 200 OK with model latency metrics when Ollama host is reachable
- [ ] Returns HTTP 503 Service Unavailable when Ollama host is unreachable or times out
- [ ] Fast unit tests pass with `dotnet test --filter "Category!=Acceptance&Category!=E2E"`
- [ ] Does not make real network calls during unit tests (uses mocked HTTP handler or fake client)

### Prerequisite Dependencies
- None (or prerequisite task titles / IDs)
