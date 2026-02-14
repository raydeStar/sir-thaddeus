# Tool-Aware E2E Harness

Conversation-level harness for replayable regression checks and score-gated iteration.

## Commands

From repo root:

- `./dev/harness.ps1 run --suite smoke --mode live --max-iters 1`
- `./dev/harness.ps1 record --suite smoke`
- `./dev/harness.ps1 replay --suite smoke`
- `./dev/harness.ps1 smoke --mode live`

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
