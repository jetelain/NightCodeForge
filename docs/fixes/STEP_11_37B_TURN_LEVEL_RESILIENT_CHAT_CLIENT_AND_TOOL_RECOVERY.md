# 🛡️ Step 11.37B: Turn-Level `ResilientChatClient`, 15-Minute Stream Stall Watchdog & Multi-Turn Tool Recovery (DROPPED)

> **Status:** Dropped / Superseded  
> *Superseded By:* [Step 11.37A: HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy](STEP_11_37A_HTTP_TRANSPORT_HARDENING_AND_OUTAGE_RETRY_POLICY.md)  
> *Reason for Dropping:* Dropped in favor of consolidating outage resilience directly into the HTTP transport layer (`TransientHttpErrorRetryHandler` with `LlmRetryPolicyOptions` and live UI activity notice hooks). Consolidating retries at the transport layer avoids stacking multi-layered retry systems and eliminates redundant higher-level client wrapping.
