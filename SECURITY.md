# Security Policy

## Supported Versions

Security fixes are applied to the current `master` branch.

## Reporting a Vulnerability

Please do not post unpatched vulnerabilities in public issues.

Report security issues by opening a private security report in GitHub (Security tab) if available. If private reporting is unavailable in your environment, open an issue with minimal details and mark it as `security` so maintainers can move discussion to a private channel.

When reporting, include:

- Affected component and version/commit
- Reproduction steps (minimal and deterministic)
- Expected vs actual behavior
- Potential impact
- Any suggested mitigation

## Response Expectations

- Initial triage target: within 3 business days
- Confirmed issues receive a remediation plan and severity classification
- Fixes are released with a short public advisory once a patch is available

## Scope Notes

This repository is local-first and designed to run on user-owned machines. Security-sensitive areas include:

- MCP tool execution boundaries
- Permission enforcement
- Audit logging and redaction
- Local data storage paths and file access
