# Testing Memory Retrieval Locally

## Quick Start

### 1. Populate Test Data

Run the test data utility to create sample facts, events, and chunks:

```powershell
dotnet run --project tools/PopulateTestMemory.csproj
```

This will:
- Create the database at `%LOCALAPPDATA%\SirThaddeus\memory.db` if it doesn't exist
- Initialize the schema (tables + FTS5 index)
- Insert test facts, events, and chunks

### 2. Run the Desktop Runtime

Start the desktop runtime normally:

```powershell
dotnet run --project apps/desktop-runtime/SirThaddeus.DesktopRuntime/SirThaddeus.DesktopRuntime.csproj
```

Or build and run the executable:
```powershell
dotnet build apps/desktop-runtime/SirThaddeus.DesktopRuntime/SirThaddeus.DesktopRuntime.csproj
.\apps\desktop-runtime\SirThaddeus.DesktopRuntime\bin\Debug\net8.0-windows\SirThaddeus.DesktopRuntime.exe
```

### 3. Test Memory Retrieval

Ask questions that should trigger memory retrieval:

**Public facts (should appear):**
- "What do I prefer?"
- "What do I like?"
- "What does the project use?"

**Events (should appear):**
- "When is my deadline?"
- "What meetings do I have?"
- "What's coming up?"

**Chunks (should appear):**
- "What did we discuss about the UI?"
- "Tell me about the memory system"
- "What are my preferences?"

**Personal items (should be blocked during technical queries):**
- Ask a technical question like "How do I debug this?" — personal items should NOT appear
- Ask a personal question like "How am I feeling?" — personal items SHOULD appear

### 4. Verify in Audit Log

Check the audit log for `MEMORY_RETRIEVED` events:

```powershell
# View recent audit events
Get-Content "$env:LOCALAPPDATA\SirThaddeus\audit.jsonl" | Select-Object -Last 20 | ConvertFrom-Json | Where-Object { $_.Action -eq "MEMORY_RETRIEVED" }
```

Or manually open: `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`

Look for entries like:
```json
{
  "actor": "agent",
  "action": "MEMORY_RETRIEVED",
  "result": "ok",
  "details": {
    "detail": "Retrieved 2 facts, 1 events, 1 context chunks for this reply."
  }
}
```

## Manual Database Inspection

### View All Facts
```powershell
sqlite3 "$env:LOCALAPPDATA\SirThaddeus\memory.db" "SELECT * FROM memory_facts WHERE is_deleted = 0;"
```

### View All Events
```powershell
sqlite3 "$env:LOCALAPPDATA\SirThaddeus\memory.db" "SELECT * FROM memory_events WHERE is_deleted = 0;"
```

### View All Chunks
```powershell
sqlite3 "$env:LOCALAPPDATA\SirThaddeus\memory.db" "SELECT chunk_id, source_type, text FROM memory_chunks WHERE is_deleted = 0;"
```

### Test FTS5 Search
```powershell
sqlite3 "$env:LOCALAPPDATA\SirThaddeus\memory.db" "SELECT c.text, fts.rank FROM memory_chunks_fts fts JOIN memory_chunks c ON c.rowid = fts.rowid WHERE memory_chunks_fts MATCH 'dark' ORDER BY fts.rank LIMIT 5;"
```

## Testing Embeddings (Optional)

Embeddings work with any OpenAI-compatible endpoint (LM Studio, Ollama,
vLLM, llama.cpp server, LocalAI, or the actual OpenAI API). Point the
base URL at whatever you're running:

1. Ensure `settings.json` has:
   ```json
   {
     "memory": {
       "enabled": true,
       "useEmbeddings": true,
       "embeddingsModel": "your-embedding-model"
     },
     "llm": {
       "baseUrl": "http://localhost:1234"
     }
   }
   ```

2. The system calls `POST /v1/embeddings` — the standard OpenAI endpoint.
3. If the endpoint isn't available, the request fails, or the model doesn't
   support embeddings, retrieval falls back to BM25-only automatically.
4. After 2 consecutive failures, the client backs off for 5 minutes to
   avoid stalling every message.

## Troubleshooting

### Memory retrieval not happening?

1. **Check settings.json** — ensure `memory.enabled` is `true`:
   ```json
   {
     "memory": {
       "enabled": true,
       "dbPath": "auto"
     }
   }
   ```

2. **Check MCP server logs** — the MCP server process writes to stderr. Look for errors about database access.

3. **Verify database exists**:
   ```powershell
   Test-Path "$env:LOCALAPPDATA\SirThaddeus\memory.db"
   ```

4. **Check audit log** — look for `MCP_SERVER_STARTED` and `MCP_SERVER_INITIALIZED` events.

### No memories retrieved?

- The database might be empty — run `PopulateTestMemory` again
- The query might not match any keywords — try broader queries
- Personal items are filtered during technical queries — try personal questions

### Embeddings not working?

- Check your LLM server is running and supports `/v1/embeddings`
- Check `ST_LLM_BASEURL` and `ST_LLM_EMBEDDINGS_MODEL` env vars are set correctly
- Note: chat models usually don't support embeddings — you need a dedicated
  embedding model (e.g. `nomic-embed-text`, `bge-base-en-v1.5`, etc.)
- The system degrades gracefully — BM25-only retrieval still works

## Direct MCP Tool Testing

You can test the MCP tool directly if you have the MCP server running:

```powershell
# Set environment variables
$env:ST_MEMORY_DB_PATH = "$env:LOCALAPPDATA\SirThaddeus\memory.db"
$env:ST_LLM_BASEURL = "http://localhost:1234"

# Run MCP server (it will read from stdin/stdout)
.\apps\mcp-server\SirThaddeus.McpServer\bin\Debug\net8.0-windows10.0.19041.0\SirThaddeus.McpServer.exe
```

Then send JSON-RPC requests via stdin (or use an MCP client).
