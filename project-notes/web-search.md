# WebSearch Tool — Decision & Implementation Rules (Paste into Cursor)

## Goal
Build a **pluggable WebSearch tool** that works for **normies (no Docker)** by default, but can **upgrade** to better providers (SearXNG / paid APIs) without refactoring.

## Decision
- **DO NOT** make Docker a hard dependency.
- Implement **provider-based WebSearch** with `mode = "auto"`:
  1) Use **SearXNG** if reachable (best/privacy, optional)
  2) Else use **DuckDuckGo HTML** (zero-install default)
  3) Else fallback to **Manual URLs** mode (user pastes links)

## UX Requirements (Normie-first)
- Default provider must work with **no installs**, **no keys**, **no Docker**.
- If SearXNG is available locally, auto-detect and use it.
- Provide a UI toggle: **“Enable Advanced Search (SearXNG)”** with guidance, but it is optional.

## Architecture Requirements
- Create interface:
  - `IWebSearchProvider.SearchAsync(query, options) -> SearchResults`
- Standardize schema:
  - `SearchResult { title, url, snippet, source }`
  - `SearchResults { results[], provider, errors[] }`
- Keep **Search** separate from **Content Extraction**:
  - Search returns URLs/snippets.
  - Extraction reads/cleans pages (Playwright/HTTP) elsewhere.

## Config
Add settings:
- `webSearch.mode`: `"auto" | "searxng" | "ddg_html" | "api" | "manual"`
- `webSearch.searxngBaseUrl`: default `http://localhost:8080`
- `webSearch.apiProvider`: optional (`brave|serpapi|bing`) + key if enabled
- `webSearch.timeoutMs`: default 8000
- `webSearch.maxResults`: default 5-10

## Provider Notes
### DuckDuckGo HTML (default)
- Implement lightweight HTML fetch + parsing (no JS).
- Add basic rate limiting / backoff.
- Expect occasional breakage; handle gracefully.

### SearXNG (optional power-user)
- If `GET {searxngBaseUrl}/` (or `/search?q=`) succeeds, prefer it in auto mode.
- Provide `docker-compose.yml` for enthusiasts, but do not require it.

### Manual URLs (fallback)
- If search fails/blocked, prompt user to paste URLs.
- Proceed with content extraction pipeline.

## Safety / Guardrails
- **No surprise networking** beyond the explicit WebSearch request.
- Log provider used + errors into audit (redact keys).
- If results are uncertain (parsing failed), say so.

## Cursor Pushback Rule (must ask, not refuse)
If any plan makes Docker mandatory:
- Ask: “Are you sure? You told me to push back when we drift. Making Docker required will block normies. Safer: keep Docker optional and default to DDG HTML with auto-detect SearXNG.”

## Deliverables
- `packages/web-search/` (or similar)
  - `IWebSearchProvider`
  - `DuckDuckGoHtmlProvider`
  - `SearxngProvider`
  - `ApiProvider` (optional stub)
  - `WebSearchRouter` (auto mode)
- Unit tests for parsing + routing
- Simple health check for SearXNG detection
