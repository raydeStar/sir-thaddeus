---
name: 'Local-Model Harness Tuning Workflow'
description: 'Pragmatic playbook for running the harness against local LLMs (2B-13B class) and iterating until numbers improve. Captures gotchas that bite every time.'
applyTo: 'tools/SirThaddeus.Harness/**,packages/agent/**,apps/headless-runtime/**,apps/mcp-server/**,src/Thaddeus.Runtime/**'
---

# Local-Model Harness Tuning Workflow

Companion to `e2e-harness-rules.instructions.md`. Those rules stay in force — this file documents the **operational loop**: verify model, baseline, triage, patch, iterate. Written from a live session where we went from 25% pass rate to stable 54%+ with targeted fixes.

Read this BEFORE running the harness on a new model size. Most of the friction this session came from skipping a step below.

---

## 0. Sanity-check the model before anything else

Changing the model in LM Studio (or llama.cpp, or Ollama, etc.) is **not enough**. The runtime reads `llm.model` from:

```
%LOCALAPPDATA%\SirThaddeus\settings.json
```

Verify from PowerShell:

```powershell
(Get-Content "$env:LOCALAPPDATA\SirThaddeus\settings.json" -Raw | ConvertFrom-Json).llm |
  Select-Object model, gatekeeperModelId, contextWindowTokens, maxTokens
```

Then confirm the LM Studio server actually has that id loaded:

```bash
curl -s http://localhost:1234/v1/models | python -m json.tool
```

If the response's `data[].id` doesn't match `llm.model`, either flip the settings or the LM Studio alias. Wrong model = wrong baseline = every fix you write is chasing a ghost.

## 1. Baseline before you patch

**Always** run the full baseline on the target model once before you change a single line of code. Failures on a 4B look very different from failures on a 2B — budgets, routing, and keyword coverage all shift.

```powershell
# Stop any stale runtime/MCP first (the harness forks its own; stale ones eat GPU)
Stop-Process -Name 'Thaddeus.Runtime','SirThaddeus.McpServer' -Force -ErrorAction SilentlyContinue

# Single suite, quick sanity
.\dev\harness.ps1 --suite smoke --judge none

# Full baseline (30-90 min on 4B, 10-20 min on 2B)
.\dev\harness.ps1 --all --judge none
```

Capture pass/fail numbers per suite before you touch anything:

```powershell
$suites = @('smoke','reasoning','web-search','quality','tool-contracts','personality','footman-validation','existence')
foreach ($s in $suites) {
  $out = .\dev\harness.ps1 --suite $s --judge none 2>&1 | Out-String
  $p = [regex]::Match($out, 'Passed: (\d+)').Groups[1].Value
  $f = [regex]::Match($out, 'Failed: (\d+)').Groups[1].Value
  "$s`t$p/$(([int]$p + [int]$f))"
}
```

Keep that table. Every patch should move a number up.

## 2. Triage: read artifacts, not just scores

Per `e2e-harness-rules.instructions.md`, the artifact folder is:

```
artifacts/harness/<run-id>/<suite>/<test>/iter-XX/
```

For local-model runs specifically, look at these patterns in `steps.jsonl` — they map to different root causes:

| Symptom in `final.txt` | Where it really came from | How to fix |
|---|---|---|
| `Cancelled` | `OperationCanceledException` inside `WorkflowChatRunCoordinator` — usually the workflow **time budget** expired mid-LLM-call | Bump `TimeBudget` in [WorkflowModels.cs](../../packages/agent/SirThaddeus.Agent/Workflow/WorkflowModels.cs) + [TaskClassifier.cs](../../packages/agent/SirThaddeus.Agent/Workflow/TaskClassifier.cs) |
| `(Tool-call loop hit its round-trip cap…)` | `ToolLoopStep` exhausted `maxRoundTrips=6` | Model is looping tools; tighten system prompt or footman policy, or bump cap |
| `LLM returned 400 (Bad Request): Context size has been exceeded` | Pipeline preamble + tool defs > model's context window | Bump `contextWindowTokens` in settings (16K → 32K for 4B) or narrow tool list via footman |
| `{"error":"Cancelled","retriable":false}` in a tool_result | Tool was mid-call when workflow cancelled | Same as the first row — budget issue |
| `(Tool) returned 0 result(s)` | Web-search DDG blocked, SearxNG down, or auto-chain fell through | See §5 below |
| Tool called but response is vague/hedged | Redacted audit summary (200 char) hides the full tool output from the **scorer**, but the LLM saw the full thing | Model limitation; check if larger model helps |

**Always cross-check `steps.jsonl` timestamps.** If `tool_result` ended at T then `final_response` appeared at T+40s with "Cancelled", the LLM draft took 40 seconds and the budget fired. That points directly at time budgets, not tool infrastructure.

## 3. Workflow time budgets — the single biggest 4B gotcha

The pipeline wraps the orchestrator in `TimeBudgetedAgentOrchestrator` (`packages/agent/SirThaddeus.Agent/Workflow/TimeBudgetedAgentOrchestrator.cs`). The budget comes from `TaskEnvelope.TimeBudget`, which `TaskClassifier` sets.

Historical ceilings (tuned for 2B qwen):
- Trivial direct-answer: 30s
- SimpleLookup: 30s
- MultiStepResearch: 60s

These are **too tight** for a 4B-class local model. A single 4B final-response draft on a longer prompt regularly takes 30-60s. Tool-loop turns can stack past 120s without doing anything wrong.

Updated ceilings (4B-safe, still well under what a 13B needs):
- Trivial direct-answer: **180s**
- SimpleLookup: **240s**
- MultiStepResearch: **600s**

These live in exactly three places — keep them synchronized or you'll spend an hour wondering which one fires:

1. `packages/agent/SirThaddeus.Agent/Workflow/WorkflowModels.cs` — `TaskEnvelope.TimeBudget` default
2. `packages/agent/SirThaddeus.Agent/Workflow/TaskClassifier.cs` — per-complexity overrides
3. `tests/SirThaddeus.Tests/Agent/Workflow/WorkflowTaskClassifierTests.cs` — assertions on the above

When you bump, update all three. Over-generous budgets hurt nothing for smaller/faster models — they return long before the timer fires.

## 4. Runtime rebuild dance

The runtime holds file locks on its output DLLs. Re-running the harness **after a code change** needs:

```powershell
# 1. Kill stale processes (both)
Stop-Process -Name 'Thaddeus.Runtime','SirThaddeus.McpServer' -Force -ErrorAction SilentlyContinue

# 2. Rebuild the pieces you changed
dotnet build packages/agent/SirThaddeus.Agent/SirThaddeus.Agent.csproj -c Debug
# and / or
dotnet build apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj -c Debug
dotnet build apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj -c Debug
dotnet build src/Thaddeus.Runtime/Thaddeus.Runtime.csproj -c Debug
```

If a build fails with `'0x00' is an invalid start of a value. LineNumber: 0` — the machine crashed mid-build and left `obj/project.assets.json` corrupted. Fix:

```powershell
Remove-Item -Recurse -Force <project>/obj, <project>/bin
dotnet restore <project>/<project>.csproj
dotnet build <project>/<project>.csproj -c Debug
```

### New NuGet dependencies don't always propagate

If you add a `PackageReference` to a class library (e.g. NCalc to `SirThaddeus.Agent`), the DLL may NOT get copied to dependent exe outputs (`Thaddeus.Runtime`, `SirThaddeus.HeadlessRuntime`, `SirThaddeus.McpServer`) without a fresh build. When the harness crashes with `Could not load file or assembly 'X'`, check:

```powershell
ls apps/headless-runtime/SirThaddeus.HeadlessRuntime/bin/Debug/net10.0/ | Select-String '<PackageName>'
```

Fix with a clean restore + build of each consuming project.

## 5. Web search backends — the ranking trap

Auto mode (`WebSearchRouter.SearchAutoAsync`) falls through this chain:

1. SearxNG (localhost:8080) — if available
2. SearchApi (paid) — if key configured
3. **DuckDuckGo** (`/html/` endpoint) — the zero-install fallback
4. GoogleNews RSS — the last resort

**Critical**: GoogleNews RSS returns query-agnostic current headlines when its `IsGenericNewsQuery` heuristic misfires. If you let it be step 3, a "latest .NET version" query ends up with Middle East war headlines and the model has to either hallucinate or refuse. DDG must come before GoogleNews (see `SearchAutoAsync` in `packages/web-search/SirThaddeus.WebSearch/WebSearchRouter.cs`).

Availability probing is flaky for DDG — the root URL sometimes serves 403 even while `/html/?q=...` searches succeed. `DuckDuckGoHtmlProvider.IsAvailableAsync` now returns `true` unconditionally; the actual search handles empty/error responses gracefully.

When diagnosing web-search failures:

```bash
# Does DDG search return real results from outside the runtime?
curl -s -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36" \
  -m 10 -X POST -d "q=your+query" "https://html.duckduckgo.com/html/" \
  -o /tmp/ddg.html
grep -c 'result__a' /tmp/ddg.html   # should be >= 5 if DDG is up
```

If that works but the runtime still gets 0 results, the bug is in `DuckDuckGoHtmlProvider` or the provider chain — not an infrastructure problem.

## 6. Deterministic fast-paths vs. "required_tools" assertions

The pipeline has multiple deterministic short-circuits (`UtilityFastPathStep` → `DeterministicUtilityEngine`). They answer directly without the LLM or tool calls.

Some harness tests assert a **specific tool must have been called** (e.g. `smoke_time_now` requires `time_now`). A deterministic fast-path bypasses that tool and hard-fails the test — even though the answer is correct.

Rules of thumb:

- **Safe to fast-path**: classic reasoning tripwires (bat-and-ball, Monty Hall), arithmetic, unit conversions, percent — these have no tool-routing assertion and test the model's ability to handle the class.
- **Do NOT fast-path** anything the tool ecosystem has a dedicated MCP tool for (`time_now`, `weather_forecast`, `holidays_is_today`, etc.). Let the LLM route; improve the tool description if it picks wrong.

Before adding a fast-path, search for a harness test that validates the specific tool:

```bash
grep -rn "required_tools" tools/SirThaddeus.Harness/Suites/ | grep <tool_name>
```

If a test requires the tool, the fast-path will break it. Prefer improving the tool's `[Description]` so the LLM picks it naturally.

## 7. Tool descriptions: be explicit for small models

Small local models (≤7B) follow tool descriptions more literally than large frontier models. Dry technical descriptions leave them guessing. What works:

1. **State when to use it**, not just what it returns.
2. **State what NOT to ask the user first** if the tool is parameter-free.
3. **Disambiguate from adjacent tools** the model might pick instead.

Example — `TimeTools.TimeNow`:

```csharp
[McpServerTool, Description(
    "Returns the CURRENT LOCAL TIME — ISO 8601, Unix milliseconds, " +
    "Windows timezone ID, and UTC offset. Takes no parameters; always " +
    "safe to call. " +
    "USE THIS TOOL when the user asks 'what time is it', 'current " +
    "time', 'time right now', or 'what time is it here'. Do NOT ask " +
    "the user for their location first — this tool reads the local " +
    "system clock directly. For time in a DIFFERENT city/country, " +
    "use weather_geocode followed by resolve_timezone instead.")]
```

Before this description, the 4B model would respond with *"could you tell me where you are?"* instead of calling `time_now`. After, it calls the tool.

## 8. Common scoring false-positives

Some failures are scoring artifacts, not real bugs. File pushback (§Pushback Protocol in the main rules) rather than patching product code for these:

- **Keyword stem mismatch**: required keyword `healthy` doesn't match model output `healthily` (substring check). If the response is semantically fine, propose a looser keyword list rather than forcing the exact word.
- **Tool tokens incorporated = 0**: the scorer reads the redacted 200-char audit summary, not the actual tool output the LLM saw. `tool_list_capabilities` returns ~20KB to the model but the scorer sees `[search: 0 result(s)]` or similar. Model citing real tool data scores 0 on this dimension. This is a known harness gap, not a product bug.
- **"Forbidden tool was called: web_search" when the test required web_search too**: happens in multi-intent tests where the assertion list is out of sync. Read the YAML; file pushback if the spec is self-contradictory.

Do NOT lower the keyword bar or reduce redaction in `ToolCallRedactor` — those are explicit harness decisions. Surface the mismatch instead.

## 9. Iteration cadence

A productive pass looks like:

1. Run one suite (`smoke` is fastest at 8 tests).
2. Pick the top 1-3 failures with the same symptom class.
3. Apply **one** targeted fix.
4. `dotnet test tests/SirThaddeus.Tests/...` — make sure unit tests still pass (they often catch regressions the harness won't).
5. Kill runtime + rebuild + rerun the same suite.
6. Confirm the target tests moved up. If not, the fix targeted the wrong layer — revert and rediagnose.
7. Loop.

Expand to wider suites (`reasoning`, `web-search`, `quality`) only after smoke stabilizes. A regression in smoke is the cheapest signal.

## 10. When to stop iterating

Stop when any of:

- Two consecutive iterations produce the same pass count (you've hit the model's ceiling).
- Remaining failures are all model-quality failures (wrong tool picked, hallucinated content) with no clear code fix.
- Remaining failures are scoring-heuristic artifacts (§8).

At that point, commit. The honest story in the commit message should match the numbers: "smoke X/8, reasoning Y/18, …" with a note on which were model limits vs. code bugs. Do not overstate progress; do not move the goalposts.

## 11. Patterns that moved the scoreboard (session retrospective)

Keep this list short and concrete. Every row is a pattern that produced a measurable jump across a full-suite run, not a theory.

| Pattern | Files touched | Typical lift |
|---|---|---|
| **Bump workflow `TimeBudget` 3-5x** when moving to a larger local model (4B/7B) | `packages/agent/SirThaddeus.Agent/Workflow/WorkflowModels.cs`, `TaskClassifier.cs`, matching tests | +3 to +5 tests (all "Cancelled" responses clear up in one shot) |
| **Reorder `SearchAutoAsync`**: DDG before GoogleNews RSS | `packages/web-search/SirThaddeus.WebSearch/WebSearchRouter.cs` | +6 web-search tests. GoogleNews RSS returns current headlines regardless of query; letting it fallback first poisons general-fact queries. |
| **Sharpen tool descriptions** — state WHEN to use, WHAT NOT to do, and DISAMBIGUATE from adjacent tools | any `[McpServerTool, Description(...)]` | +1 to +3 tests per ambiguous-tool pair. Small models follow descriptions literally. Common pairs: `time_now` vs `resolve_timezone`, `places_lookup` vs `places_discover`, `memory_retrieve` vs `memory_search`. |
| **Pattern-gated system-prompt nudges** (pipeline step, not unconditional injection) | new `*HintStep.cs` under `packages/agent/SirThaddeus.Agent/Pipeline/Steps/` | +2 tests when the nudge fires correctly, but a prompt-wide nudge caused -2 regressions elsewhere. **Always gate on a regex.** |
| **Force `Encoding.UTF8` in web-page fetches** when servers omit a charset header | `packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools/ContentExtractor.cs` | Fixes `°` → `?` mojibake. Not a test-count win but a visible UX win. |
| **Relax deterministic fast-paths that collide with tool-routing assertions** | `packages/agent/SirThaddeus.Agent/Search/DeterministicUtilityEngine.cs` | Net-zero to +1; matters when the test intent is to validate tool routing. See §6. |

### Iteration cadence that actually worked

Four full-baseline cycles got us from 46/80 (58%) → 65/80 (81%). The productive loop was:

1. **Baseline** (all 8 suites) — lock in numbers before any code change.
2. **Triage** — sort failures by `final_score` ascending; top failures are usually the same symptom class.
3. **Pick one class** — "Cancelled everywhere" (budgets), "wrong places tool" (descriptions), "didn't call web_search" (existence nudge).
4. **Apply the minimum patch** for that class.
5. **Unit tests must still pass** (`dotnet test tests/SirThaddeus.Tests/...`) — unit tests catch regressions the harness masks under variance.
6. **Rebuild all consumers** — the agent DLL flows into Thaddeus.Runtime, HeadlessRuntime, McpServer. Clean builds when DLLs don't show up in an output folder.
7. **Re-run all 8 suites**, not just the one you targeted. **Regressions in adjacent suites are the #1 sign a nudge was too broad.**
8. **Compare run-to-run variance** — a single test dropping ±1 isn't a regression, it's noise. Look for shifts of 2+ tests in the same suite before declaring a regression.
9. Stop when two cycles in a row don't move the total.

### Anti-patterns that wasted time

- **Unconditional system-prompt nudges** push small models toward over-eager tool use. Always gate on a pattern in user text.
- **Deterministic fast-paths for tool-validated tests** — my time-question fast-path bypassed `time_now` and hard-failed `smoke_time_now`. Removed. Let the LLM route; improve the tool description instead.
- **Bumping budget to a round number that feels "safe"** — start at 2-3x the old value, not the first guess. A 4B model's final-draft on a long prompt can take 40-60s; 30s was catastrophic, 180s works without visibly penalizing smaller models.
- **Trusting the first "Cancelled" diagnosis** — the cancellation could be workflow budget, tool loop round-trip cap, context-exceeded 400, LM Studio itself cancelling on OOM, or the harness disposing the sandbox. Trace timestamps in `steps.jsonl` before blaming the budget.
- **Parallel suite runs against one LLM** — the LM Studio backend serializes. Running two harness suites simultaneously just doubles the wall time and introduces false cancellations via race. Run sequentially.

### Baseline numbers worth memorizing

These are the scores that don't improve without a model upgrade. If you hit these, stop and commit. Further tuning is shuffling variance.

- **reasoning**: 17/18 on 2B qwen and 4B gemma alike. The 1 fail (`reasoning_car_wash_memory`) requires `memory_retrieve` on a prompt the model correctly short-circuits.
- **personality**: 19-21/22 on 4B. The 1-3 fails are voice/verbosity-heuristic artifacts, not content errors.
- **existence**: 0/2 without the `ExistenceVerificationHintStep`, 2/2 with it.
- **web-search**: ceiling ~12/13 on 4B. The 1 remaining fail (`web_local_business_deli`) has a flaky preference for `places_discover` even after description sharpening — close-call routing inherent to the small model.

## Appendix — useful command shortcuts

```powershell
# Stop everything the harness might collide with
Stop-Process -Name 'Thaddeus.Runtime','SirThaddeus.McpServer' -Force -ErrorAction SilentlyContinue

# Find the most recent run artifacts
Get-ChildItem artifacts/harness -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1

# Read a single test's score + response without PowerShell JSON mangling
python -c "import json; d=json.load(open(r'artifacts/harness/<run>/<suite>/<test>/iter-01/score.json','r',encoding='utf-8-sig')); print(json.dumps(d, indent=2))"

# Probe what LM Studio thinks is loaded
curl -s http://localhost:1234/v1/models | python -m json.tool

# Check which headless sandboxes still exist (they self-clean on dispose)
Get-ChildItem "$env:TEMP\SirThaddeus.Harness" -Directory -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTime -Descending | Select-Object -First 5 FullName
```
