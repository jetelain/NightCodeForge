---
name: mdfetch
description: Use this skill when fetching, downloading, or converting any web page, remote document (PDF, Word, Excel, PowerPoint, HTML), or online resource into clean Markdown using the mdfetch CLI tool.
stack: universal
---

# MarkdownFetch (`mdfetch`) Skill

This skill guides AI agents on how to fetch remote web resources and convert them into clean, structured Markdown using `mdfetch` backed by `markitdown-api`.

---

## When to Use This Skill

- Fetching and reading web documentation or articles in clean markdown format rather than raw HTML.
- Converting remote PDF manuals, reports, or specifications into text-readable markdown.
- Extracting text and tables from remote Office documents (`.docx`, `.xlsx`, `.pptx`).
- Downloading reference resources into workspace markdown files for project context or knowledge bases.

---

## Command Syntax & Common Workflows

### 1. Fetch to Standard Output (Inspect content directly)

To read the converted markdown directly in the console / subagent output:

```bash
mdfetch "https://example.com/article"
```

### 2. Save Converted Content to a File

Save markdown directly to a workspace file for indexing, reviewing, or persistent reference:

```bash
mdfetch "https://example.com/spec.html" -o "docs/spec.md"
# Or using positional argument:
mdfetch "https://example.com/spec.html" "docs/spec.md"
```

### 3. Convert Remote PDFs or Documents

`mdfetch` automatically detects MIME types and Content-Disposition headers to properly handle binary document formats:

```bash
mdfetch "https://example.com/files/whitepaper.pdf" -o "docs/whitepaper.md"
mdfetch "https://example.com/files/sheet.xlsx" -o "docs/sheet.md"
```

---

## CLI Options Reference

| Argument / Option | Short | Description | Default |
|---|---|---|---|
| `<url>` | | The URL of the web resource to fetch (required). | |
| `[<output file>]` | | Optional destination file path. If omitted, outputs to stdout. | stdout |
| `--output <path>` | `-o` | Destination file path (overrides positional argument). | stdout |
| `--timeout <sec>` | `-t` | Network and conversion timeout in seconds. | `30` |
| `--filter-hidden-text` | | Filter hidden text and invisible elements from HTML content. | `true` |
| `--no-filter-hidden-text` | | Disable filtering of hidden HTML elements. | |
| `--detect-prompt-injection` | | Detect adversarial prompt injection in fetched content. | `true` |
| `--no-detect-prompt-injection` | | Disable prompt injection detection. | |
| `--help` | `-h`, `-?` | Display usage and help message. | |

---

## Safety Guards for AI Agents

`mdfetch` has safety guards built-in to protect AI agents from untrusted web pages and malicious documents:
- **Hidden Text Filtering** (on by default): Strips invisible text/elements (`display:none`, `visibility:hidden`, `[hidden]`, `[aria-hidden="true"]`, offscreen text, zero-width spaces).
- **Prompt Injection Detection** (on by default): Rejects content attempting jailbreaks, system instruction overrides, or token injections.
- **URL Whitelisting**: Restricts requests to whitelisted hosts/patterns configured via the safety configuration.
- **Protected Safety Configuration**: In container environments, a read-only configuration at `/etc/mdfetch/safety.json` (or `%ProgramData%\MarkdownFetch\safety.json` on Windows, or fallback `MDFETCH_SAFETY_CONFIG`) can enforce safety guards and whitelist policies that agents cannot disable or tamper with. System configuration takes priority over environment variables.

---

## Error Codes and Handling

| Exit Code | Cause | Recommended Agent Action |
|---|---|---|
| `0` | Success | Proceed with the extracted markdown content. |
| `1` | URL whitelist violation | The URL (or redirect target) is not permitted by whitelist policy. Contact system administrator. |
| `1` | Prompt injection detected | Malicious prompt injection was detected in the fetched document. Fetch was aborted for security. |
| `1` | Download error (404/403/timeout) | Verify the remote URL is valid and accessible over the network. |
| `1` | Invalid CLI arguments | Ensure the URL includes the scheme (e.g. `https://...`). |
