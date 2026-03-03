# Contributing

Thanks for contributing to Sir Thaddeus.

This project is local-first, permissioned, and safety-oriented. Please keep changes deterministic, testable, and auditable.

## Ground Rules

- Keep behavior explicit. No hidden background autonomy.
- Preserve user control: approvals, time-boxed permissions, and STOP semantics.
- Prefer small, reviewable pull requests.
- Add or update tests with code changes.

## Dev Setup

Prerequisites:

- Windows
- .NET SDK (see repo tooling output via `dotnet --info`)
- PowerShell 5.1+

From repo root:

```powershell
./dev/bootstrap.ps1
```

Run locally:

```powershell
./dev/localrunner.ps1
```

Useful modes:

- `./dev/localrunner.ps1 --debug` for separate backend windows/log visibility
- `./dev/preflight.ps1` for release-style verification

## Testing

Run full test suite:

```powershell
./dev/test_all.ps1
```

Run focused tests:

```powershell
./dev/test.ps1 -Filter "FullyQualifiedName~SirThaddeus.Tests.Voice"
```

If your change touches routing, voice, permissions, or tool execution, include targeted tests for regressions.

## Code Style

- Follow existing project conventions and naming.
- Keep changes minimal and local to the problem.
- Prefer deterministic logic over prompt-only behavior where safety is involved.
- Do not commit secrets, local credentials, or private machine paths in logs.

## Pull Request Checklist

Before opening a PR:

1. Rebase/merge latest `master`.
2. Run relevant tests and include results in PR description.
3. Update docs for user-visible behavior changes (`README.md`, `SECURITY.md`, etc.).
4. Note risk areas and rollback plan for non-trivial changes.

PR description should include:

- What changed
- Why it changed
- How it was tested
- Any known limitations

## Security Issues

For vulnerabilities, do not open a full public issue first.

Use the security reporting path in [SECURITY.md](SECURITY.md).
