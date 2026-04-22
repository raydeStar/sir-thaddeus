---
name: 'Task Execution Workflow'
description: 'Rules for selecting, implementing, testing, and closing work in the Sir Thaddeus repository without requiring an external task tracker.'
applyTo: '**'
---

# Task Execution Workflow

This workflow governs how every agent operates in the Sir Thaddeus repository.
Follow these steps in sequence. Do not require Notion or any other external board to begin or complete work.

## Core Rule

- The active user request is the source of truth unless the user explicitly points you at a GitHub issue, document, or tracker.
- External trackers are optional context, not a prerequisite.
- Never block implementation on updating a task board.

---

## Progress Reporting Requirements

Progress commentary is mandatory throughout this workflow. The human operator must always know where you are, what you are doing, and whether you are stuck.

### When to report progress

Report a progress update:
- After identifying the concrete task you are working on.
- After each significant implementation step.
- After every test run, with a plain-English summary of what passed, failed, or was skipped.
- Whenever you encounter a decision point that could change the outcome.
- Whenever you are about to slow down, retry, or change approach.
- Whenever something unexpected happens (a file is missing, a build fails, a tool errors).

### What a progress update must include

Every update must state:
1. What phase you are in.
2. What you just completed.
3. What you are doing next.
4. Any open risk, uncertainty, or blocker you have noticed — even if you plan to push through it.

### Escalation thresholds

| Situation | Action |
|---|---|
| Cannot start the task at all (dependency, external resource, missing access) | Report the task as **Blocked** immediately. Do not attempt partial work. |
| Made meaningful progress but hit a hard stop you cannot resolve | Leave the work in a reviewable state and report **Needs Human Testing** with the exact blocker. |
| Approximately 90% complete but the final step requires human interaction, live UI, or unverifiable execution | Report **Needs Human Testing**. Do not self-certify. Describe the remaining step precisely. |
| Fully complete and all tests pass | Proceed to the closeout phase. |

Never stay silent when progress stalls. A blocked update is still progress information.

---

## Phase 1 – Task Framing

**Progress note:** Report the task you selected from the user's request and the verification surface you expect to use before writing code.

1. Start from the current user request, failing test, failing harness case, or explicitly named file/symbol.
2. If the user gave multiple asks, choose the highest-impact item that can be verified cleanly.
3. If a relevant issue, document, or tracker entry exists, use it as supporting context only.
4. State the objective in one or two sentences and name the files or subsystems you expect to touch.
5. If the task cannot be verified programmatically and requires manual validation, say so up front.

Report: "Selected: `<task summary>`. Verification plan: `<tests/build/harness/manual check>`. Beginning Phase 2."

---

## Phase 2 – Implementation

**Progress note:** After reading the relevant code or docs, summarize the objective and success criteria in one or two sentences so the human can confirm you understood it correctly before you go deep on changes.

1. Read the files that directly control the requested behavior.
2. Read relevant `.github/instructions/` files for the affected code area.
3. Summarize what the task requires and list the files you plan to change.
4. Implement the change. Follow existing code conventions. Do not over-engineer.
5. After each file edit, report: "Edited `<file>`: `<one sentence on what changed>`."
6. If the task requires code changes, do not create new files unless necessary.
7. If the task is documentation-only, apply the documentation standards from `documentation-standards.instructions.md`.
8. If you hit a hard blocker mid-implementation (missing dependency, unresolvable build error, external resource unavailable): stop immediately and route to **Blocked** or **Needs Human Testing** as appropriate. Do not leave the situation ambiguous.

---

## Phase 3 – Testing

**Progress note:** Before running tests, state which test type applies and why. After each test run, report the result explicitly: pass count, fail count, and what you will do next.

After implementation, run the appropriate tests for the type of change made.

### For all code changes
```powershell
dotnet build SirThaddeus.sln --no-restore -c Release
```
Must exit with code 0, zero errors, unless the task is scoped around an already-known unrelated failure that you explicitly call out.

```powershell
dotnet test SirThaddeus.sln -c Release --no-build
```
Must pass with no regressions when that slice is available. If a narrower suite is more appropriate, run the narrow suite first and explain why.

If a build or test failure cannot be resolved after two attempts with different approaches, route to **Needs Human Testing**. Do not keep retrying indefinitely.

### For agent or routing changes
Run the relevant harness suite:
```powershell
.\dev\harness.ps1 --suite <affected-suite> --judge none
```
Read `score.json` and `final.txt` artifacts, not just the summary line. See `e2e-harness-rules.instructions.md` for the full protocol.

Report: "Suite `<name>` — `<N>` passed, `<N>` failed. Score delta: `<before>` → `<after>`."

### For documentation-only changes
- Verify all links in the changed file resolve to real files in the repo.
- Verify commands cited in the document match what is actually in the repo.
- Verify no claims were added that cannot be substantiated from the codebase.
- Report: "Link check: `<N>` links verified. Command check: `<N>` commands verified. No unsubstantiated claims added."

### For tasks with no verifiable artifacts
Some tasks (screenshots, GitHub sidebar actions, demo recording, live UX checks) have no automated test surface. Treat these as requiring human verification. After completing the mechanical work, route to **Needs Human Testing**.

---

## Phase 4 – Confidence Decision

**Progress note:** State your confidence level explicitly and the specific reason for it. Do not skip this step.

After testing is complete, answer honestly:

**Am I 100% certain this change is correct, complete, and regression-free?**

This means:
- Build passes with zero relevant errors.
- All relevant existing tests pass.
- The requested success criteria are fully met.
- No behavior that worked before now breaks.
- No security regressions were introduced.
- For harness-affected changes: the relevant suite score did not drop.

### If YES → proceed to Phase 5 (commit and close)
Report: "Confidence: 100%. All requested criteria met. Proceeding to Phase 5."

### If NO → proceed to Phase 6 (Needs Human Testing)
Report: "Confidence: `<percent>`%. Uncertain because: `<specific reason>`. Proceeding to Phase 6."

When in doubt, choose Phase 6. False confidence is worse than a human review request.

### Routing guide by situation

| Situation | Route |
|---|---|
| All relevant tests pass, requested criteria met | Phase 5 → Done |
| Work is complete but cannot run automated tests (UI, screenshot, manual step) | Phase 6 → Needs Human Testing |
| ~90% done, final step requires human or live environment | Phase 6 → Needs Human Testing |
| Hard blocker, task cannot progress without external action | Phase 7 → Blocked |

---

## Phase 5 – Branch, Commit, and Close (100% confident path)

**Progress note:** Report each git command as you run it, and confirm the final commit hash in your response.

### Branch naming
Create or use a branch named for the task:
```
task/<kebab-task-name>
```

Use a short, concrete task title. Lowercase it, replace spaces with hyphens, and drop special characters.

### Commit the changes
Stage all files changed by this task:
```powershell
git add <files...>
git commit -m "<short task title>

<One paragraph summarizing what was changed and why.>"
```

Do **not** run `git push` unless the user explicitly asks.

### Closeout
- Report the branch name and short commit hash.
- Summarize what was completed.
- If there are natural next steps, list them briefly.

Report: "Branch `task/<name>` committed at `<short-hash>`. Task complete."

---

## Phase 6 – Needs Human Testing

**Triggers:**
- Confidence is less than 100% after testing.
- Work is approximately 90% complete but the remaining step requires human interaction, a live environment, or an unverifiable execution path.
- The task has no automated test surface.

**Steps:**

1. Leave the branch and uncommitted changes as-is, or commit them to a `task/<name>` branch with a `WIP:` prefix in the commit message.
2. Report all of the following:
   - What was completed.
   - What tests were run and what the results were.
   - What specific step or gap requires human review.
   - Why the agent cannot complete or verify it.
   - The branch name and commit hash, if committed.
3. State clearly that the task is in **Needs Human Testing**.
4. Do not claim the task is fully done.

---

## Phase 7 – Blocked Tasks

**Triggers:**
- The task cannot be started because of a dependency or missing resource.
- A required external resource (an API key, a live service, a platform credential) is unavailable.
- There is no path forward without a decision or action from the human operator.

**Steps:**

1. Do not attempt partial work on a blocked task unless the user explicitly asks for preparatory work.
2. Report:
   - The specific dependency or missing resource.
   - What needs to happen before work can resume.
3. State clearly that the task is **Blocked**.

---

## What You May Never Do

- Require Notion or any external task tracker before starting work.
- Block completion on updating a board, status column, or task note.
- Push to the remote repository (`git push`) without human instruction.
- Force-push, reset `--hard`, or amend published commits.
- Fabricate test results or skip the testing phase to save time.
- Self-certify a task as done when the only verification was "I reviewed the code." Tests must run when a test surface exists.
- Go silent when progress stalls. Always report what happened and what the next step is.
- Retry the same failing approach more than twice without changing strategy or escalating.

---

## Status Routing Reference

| State | When to use |
|---|---|
| **In Progress** | Immediately when you start any task. Before touching code. |
| **Done** | Only after relevant verification passes and the requested criteria are met. |
| **Needs Human Testing** | Work is complete or ~90% complete but requires human verification, live UI, or has no automated test surface. |
| **Blocked** | Task cannot start or continue due to a dependency or missing external resource. |
