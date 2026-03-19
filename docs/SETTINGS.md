# Settings Reference

This document describes fields in `SirThaddeus.Settings.template.json`.

## `llm`

- `llm.baseUrl` (string, default `http://localhost:1234`): OpenAI-compatible endpoint base URL.
- `llm.model` (string, default `local-model`): model ID used for chat completions.
- `llm.maxTokens` (int, default `2048`): max completion tokens. Must be `> 0`.
- `llm.contextWindowTokens` (int, default `8192`): estimated context window. Must be `> maxTokens`.
- `llm.temperature` (number, default `0.7`): sampling temperature in `[0, 2]`.

## `mcp`

- `mcp.serverPath` (string, default `auto`): MCP server executable path.
  - `auto` lets runtime resolve the built-in server path.

## `audio`

- `audio.ttsEnabled` (bool, default `true`): enables voice playback when supported.

## `memory`

- `memory.enabled` (bool, default `true`): enables memory retrieval/storage integration.
- `memory.dbPath` (string, default `auto`): SQLite database file path.
  - `auto` chooses the default app data location.

## `webSearch`

- `webSearch.mode` (string, default `auto`): provider strategy (`auto`, provider-specific values).
- `webSearch.searxngBaseUrl` (string, default `http://localhost:8080`): local SearXNG base URL.
- `webSearch.searxngAutoStart` (bool, default `true`): attempts to launch local SearXNG sidecar.
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
    "model": "qwen2.5-7b-instruct",
    "maxTokens": 2048,
    "contextWindowTokens": 8192,
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
