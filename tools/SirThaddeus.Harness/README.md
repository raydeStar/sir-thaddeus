# Tool-Aware E2E Harness

Conversation-level harness for replayable regression checks and score-gated iteration.

## Commands

From repo root:

- `./dev/harness.ps1 run --suite smoke --mode live --max-iters 1`
- `./dev/harness.ps1 record --suite smoke`
- `./dev/harness.ps1 replay --suite smoke`
- `./dev/harness.ps1 smoke --mode live`
- `./dev/harness_e2e.ps1` (preflight + smoke + web-search live)

Common options:

- `--max-iters <N>`
- `--min-score <0..10>`
- `--allow-workspace-edits`
- `--patch-budget-files <N>`
- `--patch-budget-lines <N>`
- `--judge cursor|none|model`
- `--judge-timeout-ms <N>`
- `--judge-required true|false`

## Suite Specs

One test file per suite entry:

- Location: `tools/SirThaddeus.Harness/suites/<suite-name>/*.yaml`
  - (Windows path in repo: `tools/SirThaddeus.Harness/Suites/<suite-name>/*.yaml`)
- Required fields:
  - `id`
  - `name`
  - `user_message`
  - `allowed_tools`
  - `mode`
  - `assertions`
  - `expectations`
  - `min_score`

## Artifacts (Flight Recorder)

Per iteration output:

- `artifacts/harness/<run-id>/<suite>/<test-id>/iter-XX/input.json`
- `steps.jsonl`
- `final.txt`
- `score.json`
- `diff.md`
- `judge_packet.json` and `judge_result.json` when judge mode is enabled

## Judge Contract (`--judge cursor`)

Harness writes `judge_packet.json` and waits for `judge_result.json`.

Expected judge schema:

```json
{
  "score": 0.0,
  "reasons": ["..."],
  "suggestions": ["..."],
  "patches": [
    {
      "file": "relative/path.cs",
      "find": "old text",
      "replace": "new text"
    }
  ]
}
```

If `--judge-required true`, missing/invalid judge output is a hard failure.

## Record / Replay Workflow

1. Record fixtures locally:
   - `./dev/harness.ps1 record --suite smoke`
2. Commit fixture updates if desired.
3. Replay in CI or local:
   - `./dev/harness.ps1 replay --suite smoke`

Replay mode uses fixture LLM/tool turns and does not call live MCP tools.

---

## Testing the Deep Dive Feature

The deep-dive pipeline returns a structured `DeepDiveBriefing` payload and renders
it in the desktop Briefing tab. Testing it properly requires covering three layers:
unit tests, harness E2E tests, and (optionally) a manual smoke test against
the live UI.

### Prerequisites

| Requirement | Where | Notes |
|---|---|---|
| .NET 8 SDK | `global.json` | Run `./dev/bootstrap.ps1` once |
| LM Studio running | `http://localhost:1234` | Or wherever `settings.json` points `llm.baseUrl` |
| Google Places API key | `settings.json` → `deepDive.placesApiKey` | Required for the Places provider path; without it, the pipeline falls back to web search |
| `settings.json` location | `%LOCALAPPDATA%/SirThaddeus/settings.json` | The harness and desktop runtime both read from this file |

### Front-to-Back Practical Setup (Recommended)

Use this workflow when you want results that mirror real app behavior end-to-end:
router -> agent -> MCP tools -> providers -> response scoring.

1. Configure `%LOCALAPPDATA%/SirThaddeus/settings.json` with live providers:

```json
{
  "llm": {
    "baseUrl": "http://localhost:1234",
    "model": "local-model"
  },
  "webSearch": {
    "mode": "auto",
    "searxngBaseUrl": "http://localhost:8080",
    "searchApiProvider": "searchapi",
    "searchApiKey": "",
    "searchApiBaseUrl": "https://www.searchapi.io/api/v1/search",
    "searchApiEngine": "google",
    "timeoutMs": 8000,
    "maxResults": 5
  },
  "deepDive": {
    "placesApiKey": "AIza...",
    "placesTimeoutMs": 8000,
    "maxToolCalls": 8,
    "maxSources": 5,
    "maxReviewSnippets": 3,
    "defaultLocale": "en-US"
  }
}
```

Notes:
- `auto` prefers local SearxNG first, then the hosted Search API when `searchApiKey` is configured, then Google News for news-style queries.
- If you have no local SearxNG running, configure `webSearch.searchApiKey` so auto mode has a general-purpose fallback.
- If `deepDive.placesApiKey` is missing, place lookups degrade to web fallback and often return lower confidence.

2. Validate external dependencies before running:

```powershell
# LM Studio health
Invoke-WebRequest -UseBasicParsing "http://localhost:1234/v1/models" | Select-Object -ExpandProperty StatusCode

# Optional: only needed for searxng mode
Invoke-WebRequest -UseBasicParsing "http://localhost:8080" | Select-Object -ExpandProperty StatusCode
```

3. Run full baseline tests:

```powershell
# One-command practical run (preflight + live suites)
./dev/harness_e2e.ps1
```

Equivalent manual commands:

```powershell
./dev/test.ps1
./dev/harness.ps1 smoke --mode live
./dev/harness.ps1 run --suite web-search --mode live
```

4. Verify deep-dive provider path from artifacts:
- Check `artifacts/harness/<run-id>/web-search/web_deep_dive_starbucks_hours/iter-01/steps.jsonl`
- Healthy place-detail run should include `places_lookup`
- If `places_lookup` is absent and only `web_search` appears, the Places key/config is not active

5. Gate criteria for "practical pass":
- Unit tests pass
- Smoke suite: all tests pass
- Web-search suite: all tests pass
- Deep-dive case returns a briefing payload with `hours`, `reviews`, and `summary` cards
- For place lookups, confidence is ideally `high`; `medium/low` is acceptable only with explicit warnings

### 1) Unit Tests (Offline, No LLM)

These run in `./dev/test.ps1` and require zero external dependencies.

```powershell
# Run everything
./dev/test.ps1

# Run only deep-dive unit tests
./dev/test.ps1 -Filter "FullyQualifiedName~DeepDiveBriefingTests"
```

**What they cover:**

| Test | Purpose |
|---|---|
| `DtoSerialization_RoundTrip_RemainsValid` | Serialization round-trip fidelity |
| `ContractCompliance_FixtureJson_DeserializeValidateAndMapProjection` | Fixture JSON → deserialize → validate → map to UI ViewModel (the "contract compliance gate") |
| `HoursParser_DetectsConflictsAcrossSources` | Deterministic hours regex parser and conflict detection |
| `CardOrdering_PlaceCards_PrioritizesWarningsAndHours` | Stable card sort order |
| `Coordinator_FallbackWithConflictingHours_AddsWarningsAndLowConfidence` | Full coordinator fallback path with conflict handling |
| `AgentOrchestrator_DeepDiveQuery_ReturnsBriefingPayload` | End-to-end orchestrator → coordinator → response with briefing |

### 2) Harness E2E Tests (Live LLM Required)

These are conversation-level integration tests that run against a live LM Studio
instance and (optionally) live MCP tools.

```powershell
# Run the entire web-search suite (all 7 tests including deep-dive)
./dev/harness.ps1 run --suite web-search --mode live

# Run only the deep-dive tests
./dev/harness.ps1 run --suite web-search --mode live --min-score 7.0
```

**Deep-dive harness tests:**

| File | ID | Query | Key Assertions |
|---|---|---|---|
| `03_deep_dive_place_briefing.yaml` | `web_deep_dive_place_briefing` | "Deep dive Portland Floral with hours + reviews..." | `places_lookup` required, response mentions "briefing" |
| `04_deep_dive_starbucks_hours.yaml` | `web_deep_dive_starbucks_hours` | "What are the operating hours of Starbucks in Olympia, WA?" | Response mentions "briefing tab", no dangerous tools |
| `05_movie_comparison_dragon.yaml` | `web_movie_comparison_dragon` | "Can you tell me if the new live action How to Train a Dragon..." | `web_search` required, response references original/live-action comparison |
| `06_product_recommendation_ashwagandha.yaml` | `web_product_recommendation_ashwagandha` | "Can you recommend a good Ashwagandha on Amazon.com?" | `web_search` required, response mentions "ashwagandha" |

**Scoring quick reference (no judge):**

The harness starts at 10.0 and deducts for:
- Missing required keywords: up to -5.0
- Forbidden keyword hits: -1.5 each
- Response over max chars: -1.0
- Response under 40 chars despite tool usage: -1.5
- "As an AI" phrasing: -0.5
- Hard assertion failures (wrong tools, missing tools): score forced to 0.0

Tests pass when `final_score >= min_score` (default 7.0) AND all hard assertions pass.

### 3) Recording Fixtures for Replay

Once a live run succeeds, record it so future runs can replay without a live LLM:

```powershell
./dev/harness.ps1 record --suite web-search
```

This writes fixtures to `tools/SirThaddeus.Harness/fixtures/web-search/`.
Then replay:

```powershell
./dev/harness.ps1 replay --suite web-search
```

### 4) Manual UI Smoke Test

To verify the briefing renders correctly in the desktop app:

1. Start LM Studio with a compatible model.
2. Ensure `settings.json` has valid `llm` and `deepDive` configuration.
3. Launch the desktop runtime.
4. Type a deep-dive trigger phrase in the command palette, e.g.:
   - "What are the operating hours of Starbucks in Olympia, WA?"
   - "Deep dive Portland Floral with hours + reviews"
5. Verify:
   - Loading state appears briefly.
   - Briefing tab auto-activates.
   - Hero card shows place name, confidence level, and status.
   - Cards render: Hours, Reviews, What to Expect, Links.
   - Warnings card appears near the top if data was incomplete.
   - Audit flyout expands and shows pipeline steps.
   - Source links are clickable and non-hallucinated.
   - Map pane shows coordinates if the Places provider returned geometry.

### 5) Configuring the Places Provider

Add the API key to `%LOCALAPPDATA%/SirThaddeus/settings.json`:

```json
{
  "deepDive": {
    "placesApiKey": "AIza...",
    "placesTimeoutMs": 8000,
    "maxToolCalls": 8,
    "maxSources": 5,
    "maxReviewSnippets": 3,
    "defaultLocale": "en-US"
  }
}
```

Without a Places API key, the deep-dive coordinator falls back to web search +
browser extraction. The fallback path still produces a valid briefing, but with
`confidence: "medium"` or `"low"` and a Warnings card explaining the degradation.

### 6) What "Good" Looks Like

| Scenario | Expected Confidence | Expected Card Count | Notes |
|---|---|---|---|
| Places API returns full data | `high` | 4+ (hours, reviews, summary, links) | Ideal path |
| Places returns partial data | `medium` | 4+ plus warnings card | Missing hours or reviews |
| Places unavailable, web fallback | `medium` or `low` | 5+ plus warnings card | Fallback sourcing noted |
| Conflicting hours across sources | `low` | Includes warnings card | Conflict explicitly surfaced |
| Complete provider failure | UI shows failure state | 0 | "No briefing payload returned" |

### 7) Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| `places_lookup` returns error | Missing or invalid API key | Set `deepDive.placesApiKey` in settings.json |
| No "briefing tab" in response | Query didn't route to deep dive | Check `IntentFeatureExtractor.LooksLikeDeepDiveLookup` trigger phrases |
| Low confidence despite good data | Warnings accumulated | Inspect `audit[]` in the briefing payload |
| Harness times out | LM Studio not running or model too slow | Check `llm.baseUrl` and model loading |
| Score = 0 in harness | Hard assertion failed | Read `score.json` in artifacts for `hard_failures` list |
