# Tool-Aware Headless Harness

Conversation-level regression harness for the real headless runtime.

## Entry point

From repo root:

- `./dev/verify-harness.ps1`
- `./dev/verify-harness.ps1 -RunLiveHarness`
- `./dev/harness.ps1 --all --judge none`
- `./dev/harness.ps1 --suite smoke --judge none`
- `./dev/harness.ps1 --category web-search --judge none`
- `./dev/harness.ps1 --test smoke_casual_no_tools --judge none`
- `./dev/harness.ps1 --suite smoke --test smoke_casual_no_tools --judge none`

`./dev/harness.ps1` automatically inserts `run` when you pass only options.
`./dev/verify-harness.ps1` runs the harness build plus rubric evaluator tests.
Use `-RunLiveHarness` only when LM Studio or another configured local model
endpoint is running.

## Selection model

- `--all` runs every suite under `tools/SirThaddeus.Harness/Suites`
- `--suite <name>` runs one suite directory
- `--category <name>` is an alias for `--suite`
- `--test <id>` runs one test id across suites and requires a unique match unless paired with `--suite`

## Common options

- `--max-iters <N>`
- `--min-score <0..1>` or legacy `<0..10>`
- `--allow-workspace-edits`
- `--patch-budget-files <N>`
- `--patch-budget-lines <N>`
- `--judge cursor|none|model`
- `--judge-timeout-ms <N>`
- `--judge-required true|false`

## Suite specs

One test file per suite entry:

- Location: `tools/SirThaddeus.Harness/Suites/<suite-name>/*.yaml`
- Required fields:
  - `id`
  - `name`
  - `user_message`
  - `allowed_tools`
  - `assertions`
  - `expectations`
  - `min_score`
- Optional fields:
  - `category`
  - `rubric_profile`: `general`, `coding`, `health`, `agentTool`, or `ragGrounded`
  - `state_setup.files[].content_base64`: bounded binary fixture bytes, mutually
    exclusive with non-empty `content`
  - `expectations.require_json`
  - `expectations.required_json_fields`
  - `expectations.forbidden_phrases`

The legacy `mode` field is still tolerated in YAML for older specs, but the
harness now runs everything through headless mode.

## Artifacts

Per iteration output:

- `artifacts/harness/<run-id>/<suite>/<test-id>/iter-XX/input.json`
- `steps.jsonl`
- `final.txt`
- `score.json`
- `diff.md`
- `judge_packet.json` and `judge_result.json` when judge mode is enabled

## Rubric scoring

Each test receives a stable `score.json` using a 0..1 overall score and 0..4
metric scores. Legacy `min_score` values like `7` are normalized to `0.7`, so
existing fixtures keep working.

Rubric profiles:

- `general`: default assistant answer quality
- `coding`: technical correctness carries extra weight
- `health`: safety boundaries carry extra weight
- `agentTool`: tool choice, tool result use, and state continuity carry extra weight
- `ragGrounded`: grounding, citation/source faithfulness, and tool use carry extra weight

Core metrics:

- `taskCorrectness`
- `instructionAdherence`
- `completeness`
- `groundingFactuality`
- `conversationality`
- `personaFit`
- `actionability`
- `concisenessFit`

Profile-specific metrics may add `technicalCorrectness`, `safetyBoundaries`,
`toolCorrectness`, `stateContinuity`, or `citationSourceFaithfulness`.

Pass/warn/fail thresholds:

- `pass`: no hard gates and score >= `0.85`
- `warn`: no hard gates and score >= `0.75` but < `0.85`
- `fail`: hard gate failure or score < `0.75`
- A harness test passes when hard gates are clear and score is at or above the
  fixture threshold. `warn` remains visible in reports as a review signal.

Hard gates include unsafe high-risk advice, fake tool actions, fabricated
citations, private/internal data leaks, ignored explicit constraints, invalid
required JSON, disallowed tools, missing required tools, and local/web fallback
non-answers that claim grounding they do not have.

## Judge contract (`--judge cursor`)

Harness writes `judge_packet.json` and waits for `judge_result.json`.

Expected judge schema:

```json
{
  "score": 0.0,
  "scores": {
    "taskCorrectness": 4,
    "instructionAdherence": 4,
    "completeness": 4,
    "groundingFactuality": 4,
    "conversationality": 4,
    "personaFit": 4,
    "actionability": 4,
    "concisenessFit": 4
  },
  "hardGateFailures": [],
  "strengths": ["..."],
  "problems": ["..."],
  "requiredFixes": ["..."],
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

`score` may be either `0..1` or legacy `0..10`; metric values must be `0..4`.
If `--judge-required true`, missing or invalid judge output is a hard failure.

---

## Testing the Deep Dive Feature

The deep-dive pipeline returns a structured `DeepDiveBriefing` payload and renders
it in the desktop Briefing tab. Testing it properly requires covering three layers:
unit tests, harness E2E tests, and (optionally) a manual smoke test against
the live UI.

### Prerequisites

| Requirement | Where | Notes |
|---|---|---|
| .NET 10 SDK | `global.json` | Run `./dev/bootstrap.ps1` once |
| LM Studio running | `http://localhost:1234` or `/v1` | Or wherever `settings.json` points `llm.baseUrl` |
| Google Places API key | `settings.json` → `deepDive.placesApiKey` | Required for the Places provider path; without it, the pipeline falls back to web search |
| `settings.json` location | `%LOCALAPPDATA%/SirThaddeus/settings.json` | The harness and desktop runtime both read from this file |

### Front-to-Back Practical Setup (Recommended)

Use this workflow when you want results that mirror real app behavior end-to-end:
router -> runtime -> MCP tools -> providers -> response scoring.

1. Configure `%LOCALAPPDATA%/SirThaddeus/settings.json` with live providers:

```json
{
  "llm": {
    "baseUrl": "http://localhost:1234",
    "model": "replace-with-loaded-model-id"
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
    "placesApiKey": "<YOUR_GOOGLE_PLACES_API_KEY>",
    "placesTimeoutMs": 8000,
    "maxToolCalls": 8,
    "maxSources": 5,
    "maxReviewSnippets": 3,
    "defaultLocale": "en-US"
  }
}
```

Notes:
- Replace `llm.model` with the exact model ID shown by your local provider. The v2 headless harness maps a blank legacy model to runtime `modelId: "auto"`, but the legacy runtime-host path expects a real model ID.
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
./dev/test.ps1
./dev/harness.ps1 --suite smoke --judge none
./dev/harness.ps1 --suite web-search --judge none
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

### 2) Headless Harness Tests (Live LLM Required)

These are conversation-level integration tests that run through the real
headless runtime against a live LM Studio instance and live MCP tools.

```powershell
# Run the entire web-search suite (all 7 tests including deep-dive)
./dev/harness.ps1 --suite web-search --judge none

# Run only the deep-dive tests
./dev/harness.ps1 --suite web-search --test web_deep_dive_place_briefing --judge none
```

**Deep-dive harness tests:**

| File | ID | Query | Key Assertions |
|---|---|---|---|
| `03_deep_dive_place_briefing.yaml` | `web_deep_dive_place_briefing` | "Deep dive Portland Floral with hours + reviews..." | `places_lookup` required, response mentions "briefing" |
| `04_deep_dive_starbucks_hours.yaml` | `web_deep_dive_starbucks_hours` | "What are the operating hours of Starbucks in Olympia, WA?" | Response mentions "briefing tab", no dangerous tools |
| `05_movie_comparison_dragon.yaml` | `web_movie_comparison_dragon` | "Can you tell me if the new live action How to Train a Dragon..." | `web_search` required, response references original/live-action comparison |
| `06_product_recommendation_ashwagandha.yaml` | `web_product_recommendation_ashwagandha` | "Can you recommend a good Ashwagandha on Amazon.com?" | `web_search` required, response mentions "ashwagandha" |

**Scoring quick reference (no judge):**

The harness runs deterministic checks first, applies hard gates, then scores the
selected rubric profile on 0..4 metrics. `overallScore` is the weighted 0..1
result. Tests pass only when the response clears hard gates and meets the
normalized `min_score`; `status` is a quality band for triage.

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
