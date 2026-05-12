# Folder access — what the assistant can see and touch

Sir Thaddeus deliberately separates **read** and **write** access to your
filesystem. They live in different code paths and different UI surfaces.

## Read-only file access (✅ live)

The assistant can read files inside folders the user has authorized
through:

- **Onboarding** → "Folder access" step (Documents on by default,
  Downloads / Desktop opt-in, plus a free-form "add a folder" input).
- **Settings → Files** → "Allowed folders (read-only)".

Behind the scenes:

| Layer | Detail |
| --- | --- |
| Settings model | [`FilesSettings.AllowedRoots`](../packages/shared-types/cs/Settings.cs) inside `SettingsDocument` |
| Settings file | `%USERPROFILE%\.thaddeus\runtime-settings.json` (managed by the web UI) |
| Env var to MCP child | `ST_DOCUMENT_READER_ALLOWED_ROOTS` (refreshed on every settings save) |
| Tools | `file_read`, `file_list` (+ their `_preview` / `_apply` dry-run pairs) in [`FileTools.cs`](../packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools/FileTools.cs) |
| Path safety | `ValidatePathAccess()` → `IsPathUnderAnyRoot()` blocks traversal & symlink escapes |
| Hard kill switch | `Files.disableAllFileAccess` setting + `ST_DOCUMENT_READER_DISABLE_FILE_ACCESS` env |

User-visible copy is explicit: *"The assistant can READ files in these
folders. It cannot write, modify, move, or delete anything."*

## Write access — through the wiki only

The assistant can create and edit markdown content in **one place**: the
**wiki** at `Documents\Sir Thaddeus Wiki\` (default; configurable).

| Layer | Detail |
| --- | --- |
| Store | [`IWikiStore`](../packages/wiki/SirThaddeus.Wiki/IWikiStore.cs) with SQLite metadata + markdown files on disk |
| UI | Dedicated `/wiki` route with editor, page tree, search, revisions |
| MCP tools | `wiki_page_create`, `wiki_page_update`, `wiki_page_revisions_list`, … (~19 tools) |
| Safety | Per-page revision history; soft-delete; restore-from-trash; rollback to any prior revision |
| Audit | Tool calls land in the per-turn JSONL trace (`<lockDir>/turns/<messageId>.jsonl`) |

Why the wiki and not a parallel store: the wiki is the only system where
the user and the assistant share the same surface with full visibility.
Versioned, hierarchical, editable in-app — and the files are real markdown
on disk if you ever need to grep them.

## What used to be here (retired)

An earlier design proposed a parallel **KnowledgeStore** feature for
"assistant-writable markdown files in an arbitrary folder you pick." It
was built but never user-facing — `enabled: false` by default, no UI,
config in a separate `settings.json` the web app didn't manage. The
audit in [`DESIGN_NOTES_2026-05.md`](DESIGN_NOTES_2026-05.md) showed it
covered the same use cases the wiki already did, so it was deleted
rather than unified.

The retirement removed:

- `packages/knowledge-store/` (the whole package)
- `KnowledgeStoreMcpTools.cs` (the 6 MCP tools)
- `KnowledgeStoreSettings` + `KnowledgeStoreRootConfig` from `AppSettings`
- Tests, harness suite (`tools/.../Suites/knowledge-store/`), and the
  `run-knowledge-store-harness.ps1` script
- The `knowledgeStore` block from `SirThaddeus.Settings.template.json`

If you had a hand-edited `settings.json` with `knowledgeStore.enabled =
true`, the field is ignored on the next boot and no data is touched.
Use the wiki for the same workflows.

## How to debug a folder-access issue

1. **Settings → Logs** shows the live `threadStoreRoot`, `logsRoot`, and
   `turnsRoot`. The Allowed-Roots list is on **Settings → Files**.
2. The runtime ships its current allowed roots to the MCP child as the
   env var `ST_DOCUMENT_READER_ALLOWED_ROOTS`. If a tool says "not under
   any allowed root", confirm the env var was set on the running child.
3. Every tool call lands in the per-turn JSONL trace
   (`<lockDir>/turns/<messageId>.jsonl`) — including `chat.tool.started`
   with `argsPreview` showing the path the assistant tried, and
   `chat.tool.completed` with the error message on failure.
