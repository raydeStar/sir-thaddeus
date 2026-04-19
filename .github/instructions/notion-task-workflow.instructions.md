---
name: 'Notion Task Workflow'
description: 'Rules for pulling tasks from the Sir Thaddeus Notion Development Board, implementing them, testing, and advancing the kanban status correctly.'
applyTo: '**'
---

# Notion Task Workflow

This workflow governs how every agent operates against the Sir Thaddeus Notion Development Board.
Follow these steps in sequence. Do not skip or reorder them.

## Board Reference

- Board: Sir Thaddeus / Development Board
- Status options (in order): Backlog → This Week → In Progress → Done / Needs Human Testing / Blocked
- Priority order: P0 — Do First > P1 — High > P2 — Medium > P3 — Low

---

## Progress Reporting Requirements

Progress commentary is mandatory throughout this workflow — not optional. The human operator must always know where you are, what you are doing, and whether you are stuck.

### When to report progress

Report a progress update:
- After completing each phase transition (e.g., "Phase 1 complete — selected task X, moved to In Progress").
- After each significant implementation step (e.g., "Edited file Y to add section Z").
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
| Cannot start the task at all (dependency, external resource, missing access) | Move to **Blocked** immediately. Report clearly. Do not attempt partial work. |
| Made meaningful progress but hit a hard stop you cannot resolve | Move to **Needs Human Testing**. Commit what exists as `WIP:`. Document exactly where it stopped and why. |
| Approximately 90% complete but the final step requires human interaction, live UI, or unverifiable execution | Move to **Needs Human Testing**. Do not self-certify. Describe the remaining step precisely. |
| Fully complete and all tests pass | Move to **Done** via Phase 5. |

Never stay silent when progress stalls. A blocked update is still progress information.

---

## Phase 1 – Task Selection

**Progress note:** Report which task you selected, its priority, and the Notion URL. Confirm you moved it to In Progress before writing a single line of code.

1. Query the Development Board for tasks with Status = "This Week", sorted by Priority ascending.
2. If there are no "This Week" tasks, fall back to Status = "Backlog" in the same priority order.
3. Pick the highest-priority task that satisfies all of these conditions:
   - Its completion can be verified with code tests, builds, or harness runs.
   - It does not depend on an incomplete task elsewhere on the board.
   - It is not already "In Progress" or "Blocked".
4. If every remaining task requires manual human interaction (live UI, GitHub sidebar clicks, screen recordings, etc.) and cannot be tested or verified programmatically, stop and report the situation clearly. Do not pick a task you cannot verify.
5. Before starting work, move the selected task to **In Progress**.
6. Report: "Selected: `<task title>` (Priority: `<P0–P3>`, Area: `<area>`). Moved to In Progress. Beginning Phase 2."

---

## Phase 2 – Implementation

**Progress note:** After reading the task page, summarize the Objective and Done When criteria in one or two sentences so the human can confirm you understood it correctly before you start coding.

1. Read the full Notion task page, including Objective, Steps, and Done When criteria.
2. Read relevant `.github/instructions/` files for the affected code area.
3. Summarize what you understood the task to require. List the files you plan to change.
4. Implement the change. Follow existing code conventions. Do not over-engineer.
5. After each file edit, report: "Edited `<file>`: `<one sentence on what changed>`."
6. If the task requires code changes, do not create new files unless necessary.
7. If the task is documentation-only, apply the documentation standards from `documentation-standards.instructions.md`.
8. If you hit a hard blocker mid-implementation (missing dependency, unresolvable build error, external resource unavailable): stop immediately, go to Phase 7 (Blocked) or Phase 6 (Needs Human Testing) as appropriate. Do not leave partial changes uncommitted and undocumented.

---

## Phase 3 – Testing

**Progress note:** Before running tests, state which test type applies and why. After each test run, report the result explicitly: pass count, fail count, and what you will do next.

After implementation, run the appropriate tests for the type of change made.

### For all code changes
```powershell
dotnet build SirThaddeus.sln --no-restore -c Release
```
Must exit with code 0, zero errors.

```powershell
dotnet test SirThaddeus.sln -c Release --no-build
```
Must pass with no regressions. If tests existed and now fail, the change broke something — diagnose and fix before proceeding.

If a build or test failure cannot be resolved after two attempts with different approaches, move to **Needs Human Testing** (Phase 6). Do not keep retrying indefinitely.

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
Some tasks (screenshots, GitHub topics, recording a demo video) have no automated test surface. Treat these as requiring human verification. Do not self-certify them as Done. After completing the mechanical work, move to **Needs Human Testing** (Phase 6).

---

## Phase 4 – Confidence Decision

**Progress note:** State your confidence level explicitly and the specific reason for it. Do not skip this step.

After testing is complete, answer honestly:

**Am I 100% certain this change is correct, complete, and regression-free?**

This means:
- Build passes with zero errors.
- All existing tests pass.
- The Done When criteria from the Notion task are fully met.
- No behavior that worked before now breaks.
- No security regressions were introduced.
- For harness-affected changes: the relevant suite score did not drop.

### If YES → proceed to Phase 5 (commit and close)
Report: "Confidence: 100%. All Done When criteria met. Proceeding to Phase 5."

### If NO → proceed to Phase 6 (Needs Human Testing)
Report: "Confidence: `<percent>`%. Uncertain because: `<specific reason>`. Proceeding to Phase 6."

When in doubt, choose Phase 6. False confidence is worse than a human review request.

### Routing guide by situation

| Situation | Route |
|---|---|
| All tests pass, all Done When criteria met | Phase 5 → Done |
| Work is complete but cannot run automated tests (UI, screenshot, manual step) | Phase 6 → Needs Human Testing |
| ~90% done, final step requires human or live environment | Phase 6 → Needs Human Testing |
| Hard blocker, task cannot progress without external action | Phase 7 → Blocked |

---

## Phase 5 – Branch, Commit, and Close (100% confident path)

**Progress note:** Report each git command as you run it, and confirm the final commit hash before writing it to Notion.

### Branch naming
Create a branch from `main`:
```
task/<kebab-task-name>
```
Examples:
- `task/add-privacy-md`
- `task/implement-check-lane`
- `task/update-github-topics`

Use the task title, lowercased, spaces replaced with hyphens, special characters dropped.

### Commit the changes
Stage all files changed by this task:
```powershell
git add <files...>
git commit -m "<task-title>

Notion task: <task-title>
Area: <Area property>
Priority: <Priority property>

<One paragraph summarizing what was changed and why.>"
```

Do **not** run `git push`. Leave the branch local for human review before it reaches the remote.

### Write the commit hash back to Notion
After committing, record the short commit hash in the task's Notes field:
```
commit: <short-hash> on branch task/<kebab-task-name>
```

### Move the Notion task to Done
Update the task Status to **Done**.

Report: "Branch `task/<name>` committed at `<short-hash>`. Notion task moved to Done."

---

## Phase 6 – Needs Human Testing (uncertain or ~90% complete path)

**Triggers:**
- Confidence is less than 100% after testing.
- Work is approximately 90% complete but the remaining step requires human interaction, a live environment, or an unverifiable execution path.
- The task has no automated test surface (screenshots, GitHub sidebar actions, demo recording, etc.).

**Steps:**

1. Leave the branch and uncommitted changes as-is, or commit them to a `task/<name>` branch with a `WIP:` prefix in the commit message:
   ```
   WIP: <task-title>
   ```
2. Write a detailed note on the Notion task that includes all of the following:
   - What was completed.
   - What tests were run and what the results were.
   - What specific step or gap requires human review.
   - Why the agent cannot complete or verify it.
   - The branch name and commit hash (if committed).
3. Move the task Status to **Needs Human Testing**.
4. Report the handoff clearly in your response so the human operator knows exactly what to do next.
5. Do not move it to Done. Do not delete the branch.

---

## Phase 7 – Blocked Tasks

**Triggers:**
- The task cannot be started because it depends on an incomplete task elsewhere on the board.
- A required external resource (an API key, a live service, a platform credential) is unavailable.
- There is no path forward without a decision or action from the human operator.

**Steps:**

1. Do not attempt partial work on a blocked task.
2. Add a note to the Notion task explaining:
   - The specific dependency or missing resource.
   - What needs to happen before work can resume.
3. Move the task to **Blocked**.
4. Report the blocker clearly in your response so the human operator can resolve it and requeue the task.

---

## What You May Never Do

- Move a task to Done without passing tests confirming the Done When criteria.
- Push to the remote repository (`git push`) without human instruction.
- Force-push, reset `--hard`, or amend published commits.
- Pick a task already owned by another agent or marked In Progress.
- Fabricate test results or skip the testing phase to save time.
- Self-certify a task as done when the only verification was "I reviewed the code." Tests must run.
- Go silent when progress stalls. Always report what happened and what the next step is.
- Retry the same failing approach more than twice without changing strategy or escalating.

---

## Status Routing Reference

| State | When to use |
|---|---|
| **In Progress** | Immediately when you start any task. Before touching code. |
| **Done** | Only after build passes, all tests pass, and all Done When criteria are met. |
| **Needs Human Testing** | Work is complete or ~90% complete but requires human verification, live UI, or has no automated test surface. |
| **Blocked** | Task cannot start or continue due to a dependency or missing external resource. |
