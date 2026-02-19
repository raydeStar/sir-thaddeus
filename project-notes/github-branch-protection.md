# GitHub Branch Protection Baseline

Use these settings after enabling workflows in `.github/workflows`.

## Target branch

- `main`

## Recommended branch protection rules

- Require a pull request before merging.
- Require at least 1 approving review.
- Dismiss stale approvals when new commits are pushed.
- Require status checks to pass before merging.
- Restrict direct pushes to `main` (except administrators if your org requires it).

## Required status checks

Use exact job names from workflow runs:

- `CI (PR) / test`

Optional release checks:

- `CI (Release Gate) / preflight`
- `CI (SBOM) / sbom`

## Merge policy

- Block merges if required checks fail.
- Do not bypass required checks for normal development.
- Keep emergency bypass permission limited to a small operator group.

## First-run setup checklist

1. Merge workflow files to `main`.
2. Open a test PR and confirm checks appear.
3. In repository settings, add required status checks.
4. Confirm failed checks block merge.
5. Confirm artifact uploads are available for failed runs.
