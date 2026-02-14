# Harness Fixtures

Record mode writes one fixture JSON per test:

- Path: `tools/SirThaddeus.Harness/fixtures/<suite>/<test-id>.json`
- Source command: `./dev/harness.ps1 record --suite <suite>`

Fixture files include:

- `llm_turns[]` for deterministic replay of model outputs
- `tool_turns[]` for deterministic replay of tool results
- `available_tools[]` observed during recording
- `metadata` with model/base-url/temperature/max-tokens

Replay and stub modes read these fixtures and do not call live MCP tools.
