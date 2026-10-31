# 🛡️ Step 12.21A: Envoy Egress Proxy Sidecar & Transparent `iptables` Container Redirection

> **Feature Implementation Step (Part 1 of 2 — Envoy Config Generator, Sidecar Lifecycle & Container Executor Integration)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.21B: Container Egress Settings UI & Live Blocked-Connection Telemetry](STEP_12_21B_CONTAINER_EGRESS_SETTINGS_UI_AND_BLOCKED_TELEMETRY.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Enforce unbypassable outbound domain whitelisting on Docker container sandboxes executing agent builds, tests, and CLI tools:
1. **Istio-Style Transparent Egress Redirection (Mode 1 — Primary):**
   - Launch an Envoy sidecar container (`ncf-envoy-task<id>`) with `--cap-add=NET_ADMIN` that configures Linux `iptables` (`OUTPUT` chain redirecting TCP 80 $\rightarrow$ 15001 and TCP 443 $\rightarrow$ 15002 for `UID != 1337`).
   - Attach the worker sandbox container directly to the sidecar's network namespace (`--net=container:ncf-envoy-task<id>`), requiring **zero privileges** in the worker container and preventing any subprocess from bypassing the proxy.
2. **Zero-Decryption TLS SNI Domain Filtering (`IEnvoyConfigGenerator`):**
   - Generate dynamic `envoy.yaml` using `envoy.filters.listener.tls_inspector` and `envoy.filters.listener.original_dst` to inspect TLS `ClientHello` SNI on port 443 and HTTP `Host` headers on port 80 without terminating TLS or requiring custom root CAs.
3. **Fallback Explicit Proxy Mode (Mode 2):**
   - When the host Docker daemon restricts `--cap-add=NET_ADMIN` (e.g. rootless Docker), automatically fall back to `--network ncf-sandbox-net` with `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` pointing to `http://ncf-egress-proxy:10001`.

*(The `/settings` Container Egress Whitelist UI tab and live blocked-connection telemetry streaming are specified in [Step 12.21B](STEP_12_21B_CONTAINER_EGRESS_SETTINGS_UI_AND_BLOCKED_TELEMETRY.md).)*

---

## 2. Architecture & Technical Specification

### 2.1 Transparent Redirection Mechanism (`IEnvoyEgressProxyManager` & `NightCodeForgeContainerExecutor`)
1. **Sidecar Launch:**
   ```bash
   docker run -d \
     --name ncf-envoy-task<id> \
     --cap-add=NET_ADMIN \
     -v <generated_envoy.yaml>:/etc/envoy/envoy.yaml:ro \
     envoyproxy/envoy:v1.31-latest
   ```
2. **`iptables` Initialization inside Sidecar Namespace:**
   Redirects all non-loopback outbound TCP port 80 (`-> 15001`) and port 443 (`-> 15002`) traffic from `UID != 1337` (Envoy UID) to Envoy listeners.
3. **Worker Sandbox Attachment:**
   [`NightCodeForgeContainerExecutor.BuildDockerRunArgs`](../../src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs) attaches `--net=container:ncf-envoy-task<id>` in `EgressRedirectionMode.Transparent` (or `--network ncf-sandbox-net` + `HTTP_PROXY`/`HTTPS_PROXY` env vars in `EgressRedirectionMode.ExplicitProxy`).

### 2.2 Envoy Configuration & SNI Filtering (`IEnvoyConfigGenerator`)
`IEnvoyConfigGenerator` renders `envoy.yaml`:
- **Listener `transparent_https_egress` (Port 15002):** Uses `original_dst` and `tls_inspector`. Matches allowed `server_names` (e.g. `api.nuget.org`, `*.nuget.org`, `registry.npmjs.org`, `github.com`) and forwards to `original_dst_cluster` (`ALLOWED` JSON access log); unmatched SNI routes to `blackhole_cluster` (`BLOCKED` JSON access log).
- **Listener `transparent_http_egress` (Port 15001):** Uses `original_dst` and `http_inspector`. Matches allowed virtual host `domains` and forwards to `original_dst_cluster`; unmatched domains return `403 Blocked by NightCodeForge Egress Policy`.
- **Pre-Configured Profiles (`EgressProfile`):** `StrictAirGapped` (0 domains), `PackageRegistriesOnly` (NuGet, npm, PyPI, Maven, Crates), `EnterpriseIntranetOnly`, and `Custom`.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Define `ContainerEgressOptions`, `EgressProfile`, and `EgressRedirectionMode` in `NightCodeForge.Core`.
- [ ] Implement `IEnvoyConfigGenerator` generating dynamic `envoy.yaml` with `tls_inspector`, `http_inspector`, `original_dst_cluster`, `blackhole_cluster`, and structured JSON stdout access logging.
- [ ] Implement `IEnvoyEgressProxyManager` managing sidecar container startup, `NET_ADMIN` capability probing, `--net=container:ncf-envoy-task<id>` lifecycle, and teardown on task container cleanup.
- [ ] Update `NightCodeForgeContainerExecutor.BuildDockerRunArgs` to support Mode 1 (Transparent `--net=container:...`) and Mode 2 (Explicit `--network ncf-sandbox-net` + `HTTP_PROXY` env vars).
- [ ] Add unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Infrastructure.Tests` verifying `envoy.yaml` generation across profiles and Docker argument generation for both redirection modes.

