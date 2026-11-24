# 🔒 Step 12.43: Secure Web UI Access & User Authentication

> **Status:** Planned (`v0.2` Security)  
> **Parent Index:** [docs/features/README.md](README.md)

---

## 1. Objective

Ensure that only the authorized developer can access the NightCodeForge Web UI (`NightCodeForge.Web`), SignalR hubs (`/hubs/orchestrator`), and management endpoints—especially when the dashboard is bound beyond `localhost` (e.g., on a LAN interface or exposed via Tailscale Serve from Step `12.16A/B`).

## 2. Requirements & Constraints

1. **Zero-Friction Local Ownership:**
   - First-run or CLI/Installer bootstrap establishes owner credentials (e.g., local password/passphrase or passkey/token) stored securely in the OS keyring (`Pmad.KeySharp`) or hashed with a modern KDF (Argon2id / PBKDF2) in local configuration.
   - Optional local-only bypass (`localhost` loopback-only mode) must be explicit and disabled automatically whenever non-loopback bindings or Tailscale Serve are enabled.
2. **Cookie & Session Security:**
   - Standard ASP.NET Core cookie authentication with `HttpOnly`, `SameSite=Lax` (or `Strict`), and `Secure` when served over HTTPS/Tailscale.
   - SignalR hub (`/hubs/orchestrator`) and Blazor Interactive Server circuits must enforce `[Authorize]` so unauthenticated clients cannot subscribe to live telemetry or invoke actions.
3. **Companion API Separation:**
   - Preserve existing companion API key authentication (`default-key.dat` from Step `12.37`) for `NightCodeAssay` and `ncf` CLI `/api/companion/*` endpoints without requiring interactive browser cookies.
4. **Air-Gapped Compatibility:**
   - Login UI and authentication flow must remain 100% self-contained with zero external identity provider or CDN dependencies.

## 3. Verification Criteria

- Unauthenticated HTTP requests to `/` and protected Razor pages redirect to `/login` (or return `401 Unauthorized` for API/SignalR endpoints).
- Unauthenticated SignalR hub connections are rejected.
- Companion API endpoints continue to authenticate via `default-key.dat` bearer/header key.
- Full test suite passes (`dotnet test --filter "Category!=Acceptance&Category!=E2E"` and Playwright E2E login flow).
