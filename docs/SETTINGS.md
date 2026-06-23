# Settings Reference

This document describes fields in `SirThaddeus.Settings.template.json`, the
legacy starter/settings-host template included with release packages. The
current web Settings UI stores the v2 runtime document at
`%USERPROFILE%\.thaddeus\runtime-settings.json`; that file uses
`llm.modelId` instead of `llm.model` and defaults to `modelId: "auto"`.

## `llm`

- `llm.baseUrl` (string, default `http://localhost:1234`): OpenAI-compatible endpoint base URL. The runtime also accepts URLs ending in `/v1`.
- `llm.model` (string, default `replace-with-loaded-model-id`): model ID used for chat completions. Replace this with the exact model ID shown by LM Studio/Ollama/provider settings before using the template as a real `settings.json`.
- `llm.gatekeeperBaseUrl` (string, default empty): optional endpoint for the gatekeeper model. Empty reuses `llm.baseUrl`.
- `llm.gatekeeperModelId` (string, default `liquid/lfm2.5-1.2b`): lightweight gatekeeper model used by the legacy runtime-host path.
- `llm.reusePrimaryModelForGatekeeperOnSharedEndpoint` (bool, default `true`): reuses the primary model when the gatekeeper shares the same endpoint.
- `llm.maxTokens` (int, default `4096`): max completion tokens. Must be `> 0`.
- `llm.contextWindowTokens` (int, default `16384`): estimated context window. Must be `> maxTokens`.
- `llm.temperature` (number, default `0.7`): sampling temperature in `[0, 2]`.
- `llm.maxRepairAttempts` (int, default `1`): retry budget for legacy repair paths.

## `mcp`

- `mcp.serverPath` (string, default `auto`): MCP server executable path.
  - `auto` lets runtime resolve the built-in server path.

## `audio`

- `audio.ttsEnabled` (bool, default `false`): enables voice playback when supported.

## `memory`

- `memory.enabled` (bool, default `true`): enables memory retrieval/storage integration.
- `memory.dbPath` (string, default `auto`): SQLite database file path.
  - `auto` chooses the default app data location.

## `webSearch`

- `webSearch.mode` (string, default `auto`): provider strategy (`auto`, provider-specific values).
- `webSearch.searxngBaseUrl` (string, default `http://localhost:8080`): local SearXNG base URL.
- `webSearch.searxngAutoStart` (bool, default `false`): attempts to launch local SearXNG sidecar.
- `webSearch.searxngLaunchCommand` (string, default `auto`): command to launch SearXNG.
- `webSearch.searxngLaunchArguments` (string, default `auto`): arguments for launch command.
- `webSearch.searxngStartupTimeoutMs` (int, default `120000`): startup timeout in ms.
- `webSearch.searchApiProvider` (string, default `searchapi`): cloud provider identifier.
- `webSearch.searchApiKey` (string, default empty): API key for cloud provider.
- `webSearch.searchApiBaseUrl` (string, default `https://www.searchapi.io/api/v1/search`): provider endpoint.
- `webSearch.searchApiEngine` (string, default `google`): engine name used by provider.
- `webSearch.timeoutMs` (int, default `8000`): request timeout in ms.
- `webSearch.maxResults` (int, default `5`): max results per request, validated to `1..20`.

## `cache`

- `cache.enabled` (bool, default `true`): enables in-memory result caching.
- `cache.webSearchTtlMinutes` (int, default `15`): TTL for web search results.
- `cache.weatherTtlMinutes` (int, default `60`): TTL for weather lookups.
- `cache.placesAndHolidaysTtlHours` (int, default `24`): TTL for places/holidays lookups.
- `cache.maxEntries` (int, default `500`): max in-memory cache entries before eviction.

## `documentReader`

- `documentReader.disableAllFileAccess` (bool, default `false`): hard-blocks all MCP file and document reads, even if folders are listed below.
- `documentReader.maxDefaultChars` (int, default `4000`): default response text cap for `document_read`.
- `documentReader.allowedRoots` (string[], default `[]`): absolute folder allowlist for `file_read`, `file_list`, and `document_read`. If empty, file access is denied until the user picks a folder.
- `documentReader.allowedExtensions` (string[], default `['.pdf','.docx','.xlsx','.csv','.rtf','.md','.txt']`): extension allowlist.

## `clipboard`

- `clipboard.enabled` (bool, default `true`): enables clipboard MCP tools.

## `activePersonalityId`

- `activePersonalityId` (string, default `helpful_default`): active personality profile key.

## Example Configurations

### LM Studio local-only

```json
{
  "llm": {
    "baseUrl": "http://localhost:1234",
    "model": "replace-with-loaded-model-id",
    "maxTokens": 4096,
    "contextWindowTokens": 16384,
    "temperature": 0.7
  }
}
```

### Cloud search fallback enabled

```json
{
  "webSearch": {
    "mode": "auto",
    "searchApiProvider": "searchapi",
    "searchApiKey": "<YOUR_KEY>",
    "maxResults": 5
  }
}
```

### Tighter document + clipboard controls

```json
{
  "documentReader": {
    "disableAllFileAccess": true,
    "maxDefaultChars": 2500,
    "allowedRoots": ["C:\\Users\\you\\Documents"],
    "allowedExtensions": [".pdf", ".docx", ".txt"]
  },
  "clipboard": {
    "enabled": false
  }
}
```
