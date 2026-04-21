# Sir Thaddeus — v1.0 Release Polish Specification

## Purpose

This document defines the work required to bring Sir Thaddeus to a **v1.0 release-quality** state: a codebase that an outside reviewer can clone, build, run, and evaluate without confusion. It is **not** a feature-expansion spec. It is a hardening, completeness, and presentation pass.

**Out of scope for this pass:** Avalonia UI polish (separate upcoming pass), new major features, architectural restructuring.

---

## Current State Summary (master @ 2026-03-18)

**Solution:** 20 packages, 3 app hosts (Avalonia UI, headless runtime, MCP server), SearXNG sidecar, voice host + backend. Targets .NET 10.0 with Windows-conditional loading for screen/OCR tools.

**Agent package (packages/agent):** 15 subdirectories, ~46 files in root, AgentOrchestrator decomposed into 13 partial classes. Contains: Context anchoring, conversation segmentation, dialogue management, guardrails, memory (auto-extract, consolidation, summarization, telemetry), orchestration, policy, post-processing (deterministic chat, source citation), routing (v2 router, footman router, arbitration policy, intent feature extraction, logic puzzle detection), search (deep dive, deterministic pre-router, entity resolver, query builder, market quotes, offline reasoning, story clustering, search orchestrator with local-business/existence-guard/pipeline partials), tool loop (completion-aware executor, budget enforcement), validation, workflow (checklist planner, confidence evaluator, retry gate/planner, task classifier, time-budgeted orchestrator, progress narrator).

**MCP tools:** Browser, ContentExtractor (web content via SmartReader + HtmlAgilityPack), Feed, File (read/preview/list with expiring previews), Holiday, Memory, Meta, Places, Screen, Status, System, Time, Timezone, Weather, WebSearch.

**Tests:** 60+ test files across SirThaddeus.Tests + SirThaddeus.Windows.Tests, with subdirectories for Continuity, Evals, Fixtures, Voice.

**Docs:** build/, migration/, runtime/ subdirectories. Root has README, README_DEPLOY, README_FIRST_RUN, README_TESTING, CONTRIBUTING, SECURITY, DISCLAIMER, CHANGELOG.

**Settings template:** LLM config (base URL, model, maxTokens, contextWindowTokens, temperature), MCP, audio, memory, webSearch (SearXNG + SearchAPI dual-provider), activePersonalityId.

---

## Section 1: Missing Base Functionality

These are capabilities a reviewer would reasonably expect from a local AI copilot for Windows that claims "lightweight document reading" and file access.

### 1.1 Local Document Reading (PDF, DOCX, XLSX)

**Current state:** `FileTools.cs` exposes `FileRead`, `FileReadPreview`, `FileList`, `FileListPreview` — all text-oriented. `ContentExtractor.cs` is a web content extractor (SmartReader + HtmlAgilityPack for URLs). There is no binary document parser.

**What to add:**

Create `packages/document-reader/SirThaddeus.DocumentReader/` with:

```
DocumentReader/
├── IDocumentReader.cs           // interface: Task<DocumentContent> ReadAsync(string path)
├── DocumentContent.cs           // record: Title, Author, PageCount, TextContent, Metadata, Format
├── DocumentFormat.cs            // enum: Pdf, Docx, Xlsx, Pptx, Rtf, Markdown, PlainText, Csv, Unknown
├── Readers/
│   ├── PdfDocumentReader.cs     // PdfPig (Apache 2.0, pure .NET)
│   ├── DocxDocumentReader.cs    // Open XML SDK (MIT, Microsoft-maintained)
│   ├── XlsxDocumentReader.cs   // Open XML SDK — extract sheet names + cell data as text
│   ├── CsvDocumentReader.cs    // CsvHelper or manual — tabular text extraction
│   ├── RtfDocumentReader.cs    // Basic RTF strip (regex-based is acceptable for v1)
│   └── PlainTextReader.cs      // Pass-through with encoding detection
├── DocumentReaderFactory.cs     // Resolves IDocumentReader by file extension
├── DocumentTruncator.cs         // Truncate extracted text to a configurable token budget
└── SirThaddeus.DocumentReader.csproj
```

**NuGet dependencies:**
- `UglyToad.PdfPig` (≥ 0.1.8) — PDF text extraction, Apache 2.0
- `DocumentFormat.OpenXml` (≥ 3.0.0) — DOCX/XLSX/PPTX, MIT

**Integration points:**
- Register `IDocumentReader` in the MCP server's DI container
- Add a new MCP tool `DocumentRead` in `FileTools.cs` (or a new `DocumentTools.cs`) that accepts a file path, resolves the reader, extracts text, truncates to budget, and returns structured content
- The tool description for the LLM must clearly state supported formats so models can route to it
- Respect the existing `FileTools` allowlisting / path-limit pattern — do not bypass file access controls

**Truncation strategy:** Accept an optional `maxChars` parameter (default: 4000). If the extracted text exceeds this, truncate with a `[...truncated — {totalChars} chars total, showing first {maxChars}]` suffix. This prevents blowing the context window on large documents.

**Test coverage required:**
- `DocumentReaderFactoryTests.cs` — correct reader resolution by extension
- `PdfDocumentReaderTests.cs` — extract text from a sample PDF fixture
- `DocxDocumentReaderTests.cs` — extract text from a sample DOCX fixture
- `XlsxDocumentReaderTests.cs` — extract sheet names + cell content
- `DocumentTruncatorTests.cs` — truncation at boundary, no truncation under limit
- Add sample fixtures to `tests/Fixtures/Documents/` (one small PDF, one DOCX, one XLSX, one CSV)

### 1.2 Clipboard Integration

**Current state:** No clipboard tool exists.

**What to add:** A new MCP tool in `mcp-tools-windows` (clipboard is OS-specific):

```csharp
// packages/mcp-tools-windows/SirThaddeus.McpTools.Windows/ClipboardTools.cs

[McpTool("ClipboardRead", "Read the current contents of the system clipboard as text")]
public static async Task<string> ClipboardRead() { ... }

[McpTool("ClipboardWrite", "Write text to the system clipboard")]
public static async Task<string> ClipboardWrite(string text) { ... }
```

**Implementation notes:**
- Use `System.Windows.Forms.Clipboard` or `Windows.ApplicationModel.DataTransfer.Clipboard` (WinRT). Since this is Windows-only and lives in `mcp-tools-windows`, WinForms clipboard is simpler and has no UWP dependency.
- Clipboard access **must** run on an STA thread. Wrap in `Task.Run` with `[STAThread]` or use `Thread` with `ApartmentState.STA`.
- `ClipboardWrite` is a **write** action — it must go through the permission gate. Classify it as a `modify` action tier, not `read`.
- `ClipboardRead` should be classified as `read` tier.
- Return a clear message if the clipboard is empty or contains non-text content (images, files).

**Test coverage:**
- `ClipboardToolsTests.cs` — mock-based tests for read/write paths, STA threading, empty clipboard handling

### 1.3 Result Caching Layer

**Current state:** No caching visible. SearXNG and SearchAPI calls appear to execute fresh every time.

**What to add:**

```
packages/core/SirThaddeus.Core/Caching/
├── IResultCache.cs              // interface: Task<T?> GetAsync<T>(string key); Task SetAsync<T>(string key, T value, TimeSpan ttl);
├── InMemoryResultCache.cs       // ConcurrentDictionary-backed with TTL expiry
└── CacheKeyBuilder.cs           // Deterministic key generation from tool name + normalized args
```

**Integration:**
- Wrap `WebSearchTools` calls with cache check. Default TTL: 15 minutes for web search, 60 minutes for weather, 24 hours for holidays/places.
- Make TTLs configurable in `SirThaddeus.Settings.template.json` under a new `"cache"` section:
  ```json
  "cache": {
    "enabled": true,
    "webSearchTtlMinutes": 15,
    "weatherTtlMinutes": 60,
    "placesAndHolidaysTtlHours": 24,
    "maxEntries": 500
  }
  ```
- Implement an LRU eviction policy when `maxEntries` is reached.
- The cache must be **in-memory only** — no persistence across app restarts. This keeps it simple and avoids stale data issues.

**Test coverage:**
- `InMemoryResultCacheTests.cs` — set/get, TTL expiry, LRU eviction, concurrent access, cache miss returns null

---

## Section 2: Code Quality and Consistency

### 2.1 Root Directory Cleanup

**Current state:** The root contains `test_health.py` in `tests/` (a Python file in a .NET project), `debug-package.ps1`, `localrunner.ps1` at root level. The root also has `agent.md` alongside the README files.

**Actions:**
- Move `tests/test_health.py` → `tools/test_health.py` (it's a utility, not a .NET test)
- Move `debug-package.ps1` → `dev/debug-package.ps1`
- Move `localrunner.ps1` → `dev/localrunner.ps1`
- Move `agent.md` → `docs/agent.md` or `project-notes/agent.md` (whichever is the intended home for design docs)
- Verify `.gitignore` covers common build artifacts: `bin/`, `obj/`, `*.user`, `*.suo`, `.vs/`, `node_modules/`, `__pycache__/`, `*.pyc`, `.idea/`, `*.db` (SQLite files), `SirThaddeus.Settings.json` (the actual settings file, not the template)
- Ensure `Microsoft/` directory at root is intentional and documented. If it contains SDK workload manifests or similar, add a one-line README inside explaining its purpose.

### 2.2 XML Documentation on Public APIs

**Current state:** Unknown coverage. For v1, all public interfaces and their methods need XML doc comments.

**Actions:**
- Enable `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in every `.csproj` that produces a package (all 20 under `packages/`)
- Enable `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` **only** in the packages that produce a library (not apps or tests). Alternatively, use `<NoWarn>` selectively — but the goal is to surface missing docs.
- **Priority public APIs to document** (these are what a contributor or reviewer will look at first):
  - `IAgentOrchestrator` — every method
  - `IRouter` / `RouterV2` — every method, plus the routing decision model
  - `IToolPermissionGate` — every method
  - `IMcpToolClient` — every method
  - `IDocumentReader` (new) — every method
  - `IResultCache` (new) — every method
  - All `*Tools.cs` files in the MCP server — the `[McpTool]` descriptions are the LLM-facing docs, but the C# methods also need `<summary>` for human readers
- Use `<inheritdoc/>` on implementing classes where the interface doc is sufficient

### 2.3 Namespace Consistency Audit

**Actions:**
- Verify that every `.cs` file under `packages/X/SirThaddeus.X/` uses namespace `SirThaddeus.X` (or a sub-namespace thereof)
- Verify that `apps/mcp-server/` files use `SirThaddeus.McpServer` or `SirThaddeus.McpServer.Tools`
- Verify that test files use `SirThaddeus.Tests` with no rogue namespaces
- If any files use `global using` directives, consolidate them into a single `GlobalUsings.cs` per project rather than scattering them

### 2.4 Consistent Error Handling Pattern

**Actions:**
- Audit all `catch` blocks across the agent package. Ensure:
  - No `catch (Exception)` that swallows silently — every catch must either log, re-throw, or return a meaningful error
  - `OperationCanceledException` is handled distinctly from general exceptions (important for the STOP kill switch)
  - Tool execution failures return structured error responses, not raw exception messages
- Verify that `AuditedMcpToolClient` logs all tool failures to the audit log with enough context to diagnose issues

---

## Section 3: Test Hardening

### 3.1 Test Organization

**Current state:** 60+ test files in a flat structure with 4 subdirectories (Continuity, Evals, Fixtures, Voice).

**Actions:**
- Create subdirectories mirroring the package structure:
  ```
  tests/SirThaddeus.Tests/
  ├── Agent/
  │   ├── Routing/
  │   ├── Search/
  │   ├── Memory/
  │   ├── Workflow/
  │   ├── ToolLoop/
  │   └── Guardrails/
  ├── Caching/
  ├── DocumentReader/
  ├── Continuity/
  ├── Evals/
  ├── Fixtures/
  │   └── Documents/          ← new: sample PDF, DOCX, XLSX, CSV
  ├── Integration/
  ├── MCP/
  └── Voice/
  ```
- Move existing test files into the appropriate subdirectory. The mapping should be:
  - `RouterTests.cs`, `RouterV2Tests.cs`, `RoutingAccuracyTests.cs`, `FootmanRouterTests.cs`, `FootmanRecalibrationTests.cs`, `IntentFeatureExtractorTests.cs` → `Agent/Routing/`
  - `MemoryContextProviderTests.cs`, `MemoryRetrievalTests.cs` → `Agent/Memory/`
  - `GuardrailsCoordinatorTests.cs`, `GuardrailsPipelineTests.cs`, `PromptInjectionGuardTests.cs` → `Agent/Guardrails/`
  - `PolicyGateTests.cs`, `PermissionBrokerTests.cs` → `Agent/` (or `Agent/Policy/`)
  - `McpSharedTests.cs`, `AuditedMcpToolClientTests.cs`, `PublicApiIntegrationTests.cs`, `PublicApiProvidersTests.cs` → `MCP/`
  - `LmStudioClientTests.cs`, `OpenAiEmbeddingClientTests.cs`, `HelperModelTimeoutTests.cs` → `Integration/`
  - etc.
- **Do not rename test classes** — only move files. This keeps git blame intact.

### 3.2 Missing Test Coverage

**Gaps identified (tests that should exist but don't appear to):**
- `ContentExtractorTests.cs` — no visible tests for web content extraction
- `ToolLoopExecutorTests.cs` — the ToolLoop directory has 3 classes but no dedicated test file
- `SearchOrchestratorTests.cs` — the search subsystem is extensive (25 files) but no dedicated search orchestrator tests visible
- `PersonalityEngineTests.cs` exists but `PersonalityV15Tests.cs` suggests a version migration — ensure both paths are tested
- `ClipboardToolsTests.cs` (new)
- `DocumentReaderTests.cs` (new, multiple — see section 1.1)
- `InMemoryResultCacheTests.cs` (new)
- `ConversationSegmentationTests.cs` exists — verify it covers the recent March 14 changes

**Actions:**
- Add the missing test files listed above
- Run `dotnet test` with `--collect:"XPlat Code Coverage"` and generate a coverage report. Target ≥ 70% line coverage for `packages/agent` and ≥ 80% for `packages/core`, `packages/memory`, `packages/memory-sqlite`
- Ensure all tests pass with `dotnet test SirThaddeus.sln` — the changelog says "solution build is green" but test status should be explicitly verified

---

## Section 4: Documentation for Outside Review

### 4.1 Architecture Documentation

**Current state:** The README contains a Mermaid flowchart of the five-layer architecture. This is good but not sufficient for a reviewer.

**What to add:** Create `docs/ARCHITECTURE.md`:

```markdown
# Architecture Overview

## Five-Layer Architecture
[Existing Mermaid diagram from README]

## Package Map
| Package | Responsibility | Key Interfaces |
|---------|---------------|----------------|
| agent | Route, gate, validate, repair, complete | IAgentOrchestrator, IRouter |
| audit-log | Structured JSON-line audit logging | IAuditLogger |
| config | Settings loading and validation | (settings models) |
| contracts | Shared DTOs and interfaces | (contract types) |
| core | Cross-cutting utilities | IResultCache |
| invocation | Tool invocation abstractions | (invocation models) |
| llm-client | OpenAI-compatible HTTP client | ILlmClient |
| local-tools | Playwright-based browser automation | (tool classes) |
| mcp-shared | Shared MCP protocol types | (MCP models) |
| mcp-tools-core | Cross-platform MCP tools | (tool classes) |
| mcp-tools-windows | Windows-only MCP tools (screen, OCR, clipboard) | (tool classes) |
| memory | Memory abstractions | IMemoryProvider |
| memory-sqlite | SQLite-backed memory store | (SQLite provider) |
| observation-spec | Observation schema validation | ObservationSpecValidator |
| permission-broker | Time-boxed permission tokens | IPermissionBroker |
| personality-engine | AI personality configuration | IPersonalityEngine |
| runtime-host | Shared runtime setup (LLM options, MCP env, paths) | (host builder) |
| tool-runner | Tool execution with budget enforcement | IToolRunner |
| voice | Voice transport abstractions | (voice models) |
| web-search | SearXNG + SearchAPI dual-provider | IWebSearchProvider |
| document-reader (new) | PDF/DOCX/XLSX text extraction | IDocumentReader |

## Agent Orchestrator Decomposition
The AgentOrchestrator is decomposed into partial classes:
- `AgentOrchestrator.cs` — entry point, main RunAsync loop
- `AgentOrchestrator.Routing.cs` — intent classification and route dispatch
- `AgentOrchestrator.LlmInteraction.cs` — model prompt construction and response parsing
- `AgentOrchestrator.Memory.cs` — memory retrieval and injection
- `AgentOrchestrator.MemoryFallback.cs` — graceful degradation when memory is unavailable
- `AgentOrchestrator.WebSearch.cs` — web search integration
- `AgentOrchestrator.MultiIntent.cs` — compound query decomposition
- `AgentOrchestrator.ContextAnchoring.cs` — context window management
- `AgentOrchestrator.HistoryPersistence.cs` — conversation history save/restore
- `AgentOrchestrator.Personality.cs` — personality injection
- `AgentOrchestrator.QueryExtraction.cs` — search query generation
- `AgentOrchestrator.SessionState.cs` — session lifecycle
- `AgentOrchestrator.UtilityExecution.cs` — deterministic utility execution
- `AgentOrchestrator.Internal.cs` — private shared helpers

## Routing Pipeline
1. IntentFeatureExtractor classifies the incoming message
2. DeterministicPreRouter checks for utility/math/conversion shortcuts
3. RouterV2 selects the execution strategy (direct chat, search, tool use, workflow)
4. RouteArbitrationPolicy resolves conflicts between router and footman
5. PolicyGate enforces tool access rules and budgets
6. ToolLoop executes approved actions with completion awareness

## Search Pipeline
1. SearchModeRouter determines search type (web, local business, existence check)
2. QueryBuilder generates optimized search queries
3. SearchOrchestrator executes search via SearXNG or SearchAPI
4. WebArticleContentFetcher retrieves full article content when needed
5. StoryClustering groups related results
6. SearchResponseFormatter produces the final response with source citations
7. SourceCitationFormatter adds structured citations in post-processing
```

### 4.2 Settings Documentation

**Current state:** `SirThaddeus.Settings.template.json` exists but has no accompanying documentation explaining what each field does.

**What to add:** Create `docs/SETTINGS.md`:

Document every field in the settings template with:
- Field name and JSON path
- Type and default value
- What it controls
- Valid values / ranges
- Example configurations (e.g., "using Ollama instead of LM Studio", "using a cloud search API instead of SearXNG")

Also add the new `cache` section and the `documentReader` section (if any config is needed — likely just `maxDefaultChars`).

### 4.3 README Polish

**Actions on the main README.md:**
- Add a **Supported Document Formats** line under the "Local AI Runtime" features section: "PDF, DOCX, XLSX, CSV, RTF, Markdown, and plain text reading from local files"
- Add a **Clipboard** line under "Permissioned Tooling via MCP": "Clipboard read/write for seamless copy/paste integration"
- Add a **Caching** line under "Local AI Runtime": "In-memory result caching with configurable TTLs for web search, weather, and location data"
- Ensure the project structure diagram matches the actual directory layout (verify `Microsoft/` is listed or explained)
- Add a "Development" section with:
  ```
  ## Development

  ### Prerequisites
  - .NET 10.0 SDK
  - (Optional) LM Studio or any OpenAI-compatible local model server
  - (Optional) SearXNG for local web search (bundled setup available)

  ### Build
  dotnet build SirThaddeus.sln

  ### Test
  dotnet test SirThaddeus.sln

  ### Run (headless)
  dotnet run --project apps/headless-runtime/SirThaddeus.HeadlessRuntime

  ### Run (hybrid runtime)
  dotnet run --project src/Thaddeus.Runtime/Thaddeus.Runtime.csproj
  ```

### 4.4 CONTRIBUTING.md Audit

**Actions:**
- Verify it describes how to set up the development environment
- Verify it explains the package structure and where new code should go
- Add a "Adding a New MCP Tool" section as a worked example
- Add a "Adding a New Package" section explaining the naming convention and where to add the project reference in the solution

---

## Section 5: Build and CI Hygiene

### 5.1 Solution File Audit

**Actions:**
- Run `dotnet sln SirThaddeus.sln list` and verify every project under `packages/`, `apps/`, and `tests/` is included
- Verify the new `document-reader` project is added to the solution
- Ensure solution folders match the directory structure (packages, apps, tests)

### 5.2 Package Dependency Audit

**Actions:**
- Run `dotnet list SirThaddeus.sln package --outdated` and update any packages with known security vulnerabilities
- Verify no circular references between packages (the dependency graph should be a DAG)
- Ensure `packages/core` does not depend on `packages/agent` (core should be a leaf dependency)
- Verify that `mcp-tools-windows` has a conditional `<TargetFramework>` or runtime check so it doesn't break Linux/macOS builds

### 5.3 GitHub Actions / CI

**Current state:** `.github/` directory exists. Verify it contains:
- A build workflow that runs `dotnet build` and `dotnet test` on every PR
- If not present, add a minimal CI workflow:

```yaml
# .github/workflows/ci.yml
name: CI
on: [push, pull_request]
jobs:
  build-and-test:
    runs-on: windows-latest  # Windows required for mcp-tools-windows
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore SirThaddeus.sln
      - run: dotnet build SirThaddeus.sln --no-restore --configuration Release
      - run: dotnet test SirThaddeus.sln --no-build --configuration Release --logger "trx;LogFileName=test-results.trx"
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: '**/test-results.trx'
```

### 5.4 .editorconfig

**Actions:**
- If `.editorconfig` doesn't exist at the repo root, add one with standard C# conventions:
  - `indent_style = space`, `indent_size = 4`
  - `dotnet_sort_system_directives_first = true`
  - `csharp_style_namespace_declarations = file_scoped`
  - `csharp_style_var_for_built_in_types = true`
  - `dotnet_naming_rule` entries for PascalCase on public members, camelCase on private fields with `_` prefix

---

## Section 6: Settings and Configuration Completeness

### 6.1 Settings Template Updates

Add the following sections to `SirThaddeus.Settings.template.json`:

```json
{
  "cache": {
    "enabled": true,
    "webSearchTtlMinutes": 15,
    "weatherTtlMinutes": 60,
    "placesAndHolidaysTtlHours": 24,
    "maxEntries": 500
  },
  "documentReader": {
    "maxDefaultChars": 4000,
    "allowedExtensions": [".pdf", ".docx", ".xlsx", ".csv", ".rtf", ".md", ".txt"]
  },
  "clipboard": {
    "enabled": true
  }
}
```

### 6.2 Settings Validation

**Actions:**
- Ensure the config package validates settings on startup and logs clear warnings for missing or invalid values
- Ensure the app doesn't crash on missing optional settings — all new sections should have sensible defaults
- Add validation for: `maxTokens > 0`, `contextWindowTokens > maxTokens`, `temperature` between 0 and 2, `maxResults` between 1 and 20, file paths exist or are "auto"

---

## Section 7: Security and Privacy Review

### 7.1 File Access Controls

**Actions:**
- Verify that `FileTools` and the new `DocumentReader` enforce an allowlist of accessible directories
- Verify that path traversal attacks (`../../etc/passwd` style paths) are blocked
- Verify that the file tools cannot read outside the configured allowed paths even if the LLM crafts a malicious path argument
- Document the file access policy in `SECURITY.md`

### 7.2 Clipboard Security

**Actions:**
- `ClipboardRead` must not be invokable without user permission (it could expose sensitive copied data like passwords)
- Classify `ClipboardRead` as a `sensitive-read` tier in the permission broker, requiring explicit user approval each time (not blanket session permission)
- `ClipboardWrite` should be `modify` tier

### 7.3 Audit Log Completeness

**Actions:**
- Verify that every MCP tool invocation (including the new clipboard and document reader tools) is logged to the audit trail
- Verify that the audit log captures: tool name, arguments (with sensitive values redacted by `ToolCallRedactor`), result summary, timestamp, permission token used, execution duration
- Verify that `ClipboardRead` results are redacted in the audit log (clipboard may contain sensitive data)

---

## Execution Order

Recommended implementation sequence (each step should result in a green build + green tests):

1. **Root cleanup** (Section 2.1) — move files, update paths, 15 minutes
2. **Document reader package** (Section 1.1) — new package + tests, ~2 hours
3. **Result cache** (Section 1.3) — new utility + integration + tests, ~1 hour
4. **Clipboard tools** (Section 1.2) — new tool + tests, ~45 minutes
5. **Settings updates** (Section 6) — template + validation, ~30 minutes
6. **Test reorganization** (Section 3.1) — move files into subdirectories, ~30 minutes
7. **Missing test coverage** (Section 3.2) — write new tests, ~2 hours
8. **XML documentation** (Section 2.2) — add doc comments to public APIs, ~2 hours
9. **Architecture docs** (Section 4.1) — write ARCHITECTURE.md, ~1 hour
10. **Settings docs** (Section 4.2) — write SETTINGS.md, ~30 minutes
11. **README polish** (Section 4.3) — update README, ~30 minutes
12. **CONTRIBUTING audit** (Section 4.4) — update CONTRIBUTING.md, ~30 minutes
13. **Namespace + error handling audit** (Sections 2.3, 2.4) — review + fix, ~1 hour
14. **Build/CI hygiene** (Section 5) — solution audit, CI workflow, .editorconfig, ~1 hour
15. **Security review** (Section 7) — verify controls, update SECURITY.md, ~1 hour

**Estimated total:** ~14 hours of focused implementation work

---

## Acceptance Criteria

A reviewer cloning the repo for the first time should be able to:

1. `dotnet build SirThaddeus.sln` — builds cleanly with zero warnings on Release configuration
2. `dotnet test SirThaddeus.sln` — all tests pass, ≥ 70% line coverage on core packages
3. Read `README.md` and understand what the project does, how to run it, and how it's structured
4. Read `docs/ARCHITECTURE.md` and understand the package map, routing pipeline, and search pipeline without reading source code
5. Read `docs/SETTINGS.md` and configure the app for their local setup without guessing
6. Say "read the PDF on my desktop" and get text extracted from a local PDF file
7. Say "what's on my clipboard" and get clipboard contents (with permission prompt)
8. Observe that repeated web searches for the same query within the TTL window return cached results
9. Find no loose development artifacts at the repo root
10. See consistent naming, error handling, and documentation across all packages