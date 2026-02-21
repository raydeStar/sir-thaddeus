---
description: How to perform a self-fixing cycle using the E2E harness without cheating.
---

# E2E Self-Fixing & Anti-Cheat Protocol

Follow this workflow when using the E2E harness to identify and fix bugs.

## 1. Run Baseline
Execute the harness to identify current failures.
```powershell
.\dev\harness_e2e.ps1
```
Or for a specific suite:
```powershell
.\dev\harness.ps1 run --suite <name> --mode live
```

## 2. Analyze Failures (Without Cheating)
- **DO NOT** open the `.yaml` test files to find "correct" keywords or expected strings.
- **DO** read the `artifacts/harness/` output logs to see the actual error or missing behavior.
- **DO** look for logic gaps or tool failures in the agent logs.

## 3. Implement Fix (First Principles)
- Solve the problem by improving the underlying logic, prompts, or tool integration in `packages/agent/`.
- **NO HARDCODING**: Never add `if (input.Contains("test_id"))` or hardcode the expected answer string.
- The fix must work for *any* similar input, not just the specific test case.

## 4. Provide Proof of Work (The Trace)
When proposing a fix, include a "Derivation Trace":
- **Inputs Used**: Which parts of the user message or tool results informed the fix?
- **Reasoning**: Explain *why* the code change solves the problem generally.
- **Tools Used**: List any tools that were essential to deriving the solution.

## 5. Verify and Loop
Run the harness again. If the score doesn't improve, return to step 2.

> [!WARNING]
> Cheating (hardcoding answers) results in a score of **0** for the entire run. If you detect drift, use the Pushback Template.
