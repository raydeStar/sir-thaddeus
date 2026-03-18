# Security Policy

## Supported Versions

Security fixes are applied to:

- The current `master` branch
- The most recent packaged release

Older tags/branches may not receive backported fixes.

## Reporting A Vulnerability

Do not disclose unpatched vulnerabilities in public issues.

Preferred path:

1. Open a private GitHub security advisory/report (Security tab).

Fallback path (if private reporting is not available):

1. Open a public issue with minimal detail.
2. Mark it with `security`.
3. Include only non-exploit details so maintainers can move discussion private.

Please include:

- Affected component(s) and commit/version
- Reproduction steps (minimal, deterministic)
- Expected vs actual behavior
- Security impact and likely attack surface
- Logs, traces, or proof-of-concept (sanitized)
- Any mitigation or patch suggestion

## Response Expectations

- Initial triage target: within 3 business days
- Confirmed issues get severity + remediation plan
- Fixes are disclosed publicly after a patch is available

## Security Scope

Sir Thaddeus is local-first and permissioned. Priority security areas:

- Layer 1 (Loop): policy gating, tool budget enforcement, action validation/repair
- Layer 2 (Interface): explicit user approvals and STOP behavior
- Layer 4 (Tools): MCP tool boundaries, allowlists, command execution controls
- Layer 5 (Voice): local voice host/backend process boundaries and local endpoints
- Audit trail: append-only logging, redaction, and operator-visible actions
- Local data: memory/database paths, file access scope, and profile isolation

## File Access Policy

File operations are allowlist-bound and normalized before execution.

- `file_read`, `file_list`, and `document_read` resolve paths to canonical full paths.
- Requests outside configured allowlisted roots are denied.
- Path traversal patterns (for example `..\..\`) are blocked by canonical-path containment checks.
- Tool-level previews/applies and runtime policy gates enforce the same bounds.

## Clipboard Policy

Clipboard access is treated as sensitive.

- `clipboard_read` is a per-call sensitive-read operation and always requires explicit approval.
- `clipboard_read` approvals are not persisted as session/always grants.
- `clipboard_write` is treated as a modify/system action and remains permission-gated.
- Audit summaries redact clipboard payloads (size/hash metadata only) to avoid leaking copied secrets.

## Out Of Scope

The following are generally out of scope unless they create a clear security bypass:

- Requests for unsupported/legacy branch fixes
- Local misconfiguration that does not bypass permissions
- Issues requiring physical access to an unlocked machine
- Third-party model quality/safety behavior not caused by this runtime

## Disclosure And Credit

When possible, advisories include:

- Affected versions
- Severity
- Mitigation guidance
- Fix commit or release reference

Reporter credit is included unless anonymity is requested.
