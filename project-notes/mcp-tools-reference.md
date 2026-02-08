# MCP Tools Reference

A plain-language guide to every tool the assistant can use. Each tool runs in a separate process (the MCP server) — the assistant itself has no direct access to your files, screen, or the internet. It has to go through one of these tools, and every call is logged.

---

## Memory Tools

These tools let the assistant remember things about you and recall them later. Everything is stored locally in a small database on your computer — nothing is sent anywhere.

### MemoryRetrieve

**What it does:** Looks through everything the assistant has been asked to remember — your facts, past events, conversation notes — and pulls back the most relevant bits for the current conversation.

**When it's used:** Automatically, at the start of every message you send. You don't have to ask for it. It runs quietly in the background so the assistant can give you more personalized answers.

**Special mode — "greet":** When you open a new conversation and say something like "hey" or "good morning," the assistant uses a lightweight version of this tool. It only grabs your name and a couple of quick personal facts (like your communication style preference) instead of digging through everything. Keeps the greeting snappy.

**What it returns:** A pre-built text block that gets injected into the assistant's context, plus your profile card and any relevant "nuggets" (small personal facts you've saved). Also returns metadata like how many facts, events, and nuggets were found.

**Safety:** Read-only. It can't change anything — it just looks things up.

---

### MemoryStoreFacts

**What it does:** Saves a new fact you've told the assistant to remember. Facts are stored as simple three-part statements: **who** → **does/is what** → **the thing**. For example: *"user" → "likes" → "dark mode"*.

**When it's used:** When you say things like:
- "Remember that I prefer dark mode"
- "Note that my wife's name is Sarah"
- "I like pizza"
- "My favorite color is blue"

**Smart conflict handling:** Before saving anything, it checks for:
- **Duplicates** — if you already said "I like pizza," it won't save it again.
- **Contradictions** — if you already said "I like light mode" and now say "I prefer dark mode," it flags the conflict and asks you what to do instead of silently overwriting.
- **Opposite statements** — if you say "I hate pizza" but there's already a fact that says "I like pizza," it catches that too.

**What it returns:** A summary of what was saved, what was skipped (duplicates), and any conflicts that need your input.

**Safety:** Only runs when you explicitly ask the assistant to remember something. It never silently saves things on its own.

---

### MemoryUpdateFact

**What it does:** Changes an existing fact after you've confirmed a conflict resolution. This is the follow-up to `MemoryStoreFacts` when it finds a contradiction.

**When it's used:** After the assistant asks you something like: *"You previously said you like light mode, but now you're saying dark mode. Should I update it?"* — and you say yes.

**What it returns:** Confirmation that the old fact was updated with the new value.

**Safety:** Only runs after you explicitly confirm the change. The assistant can't update your memories without your say-so.

---

## Web Tools

### WebSearch

**What it does:** Searches the internet and reads the top results for you. It doesn't just give you a list of links — it actually visits the top pages, extracts the article content, and gives the assistant the real text so it can summarize what it found.

**When it's used:** When you ask about anything that needs current information:
- "What happened in the news today?"
- "What's the weather like?"
- "How much does a PS5 cost right now?"

**How it works under the hood:**
1. Sends your query to a search engine (DuckDuckGo, Google News RSS, or SearXNG — whichever is available).
2. Grabs the top 5 results.
3. Actually visits each page and extracts the article text.
4. Removes junk (ads, cookie banners, navigation menus).
5. Gives the assistant clean article excerpts to summarize.

**What it returns:** Article summaries for the assistant to synthesize, plus structured data (titles, URLs, favicons) so the UI can show you clickable source cards.

**Limits:** Up to 10 results, 8-second search timeout, 10 seconds per page, and excerpts capped at ~250 characters each.

---

### BrowserNavigate

**What it does:** Fetches a specific web page when you already have the URL. Think of it as "go read this page for me."

**When it's used:** When you paste a URL and say "read this" or "what does this page say?" — or when the assistant needs to follow up on a search result.

**What it returns:** The page title, author, publish date, and the article's full text (cleaned up, no ads or navigation junk). Truncated to ~4,000 characters to keep things focused.

**Limits:** 20-second timeout, single page only (no clicking around or crawling).

---

## File Tools

### FileRead

**What it does:** Reads the contents of a file on your computer and gives the text to the assistant.

**When it's used:** When you ask the assistant to look at a specific file — a log, a config, a document, etc.

**What it returns:** The full text content of the file.

**Limits:** Maximum file size is 1 MB. Anything bigger gets rejected with an error. This is a safety measure to prevent the assistant from accidentally loading a massive file and choking.

**Safety:** Read-only — it cannot modify, create, or delete files.

---

### FileList

**What it does:** Lists the files and folders inside a directory on your computer. Like opening File Explorer and glancing at what's there.

**When it's used:** When the assistant needs to see what files exist in a folder — for example, to find a config file or see the structure of a project.

**What it returns:** A list of file and folder names, marked with `[FILE]` or `[DIR]` so it's clear which is which.

**Limits:** Maximum 100 entries per call. If a folder has more than 100 items, it only shows the first 100.

**Safety:** Read-only — it only looks, never touches.

---

## System Tools

### SystemExecute

**What it does:** Runs a command on your computer's command line (like typing something into Command Prompt) and returns the output.

**When it's used:** When you ask things like:
- "Who am I logged in as?" (runs `whoami`)
- "What's my computer name?" (runs `hostname`)
- "What's my IP address?" (runs `ipconfig`)

**Strict safety — allowlist only:** This tool can only run a small, pre-approved list of commands:

| Allowed Command | What It Does |
|---|---|
| `whoami` | Shows your Windows username |
| `hostname` | Shows your computer's name |
| `date` | Shows the current date |
| `time` | Shows the current time |
| `echo` | Repeats text back (used for simple checks) |
| `dir` / `ls` | Lists files in a directory |
| `type` | Shows file contents |
| `where` | Finds where a program is installed |
| `systeminfo` | Shows system details (OS version, RAM, etc.) |
| `ipconfig` | Shows network/IP information |
| `dotnet` | Runs .NET commands |

If the assistant tries to run anything not on this list, the tool flat-out refuses and tells it what's allowed.

**Safety:** Cannot run arbitrary commands. No `del`, no `format`, no `powershell`, no `curl` — nothing dangerous. The allowlist is hard-coded.

---

## Screen Tools

### ScreenCapture

**What it does:** Takes a picture of your screen, then reads all the text it can see using built-in Windows OCR (text recognition). The assistant gets the extracted text — not the image itself.

**When it's used:** When you say things like:
- "Look at my screen"
- "What do you see?"
- "Can you read what's on my monitor?"

**Two capture modes:**
- **`full_screen`** (default) — captures your entire monitor. This is what's used almost always.
- **`active_window`** — captures only the currently focused window. Only used if you specifically say "this window" or "the active window."

**What it returns:** A text report with:
- What window is currently active (title + app name)
- Your screen resolution
- All readable text extracted from the screenshot via OCR

**Limits:** OCR text is capped at 8,000 characters. Single snapshot — no video or continuous capture.

**Safety:** Observation-only — it looks at the screen but can't click, type, or change anything.

---

### GetActiveWindow

**What it does:** Tells the assistant which window you currently have in the foreground — the app name, window title, and process ID.

**When it's used:** As a lightweight alternative to full screen capture when the assistant just needs to know what app you're using, not what's on your screen.

**What it returns:** Three lines: the window title, the process name (like "chrome" or "code"), and the process ID.

**Safety:** Extremely lightweight, read-only. No screenshots, no OCR, no screen content.

---

## Summary Table

| Tool | Category | Read or Write? | Needs Your Permission? |
|---|---|---|---|
| MemoryRetrieve | Memory | Read | No (runs automatically) |
| MemoryStoreFacts | Memory | Write | Yes (you must ask it to remember) |
| MemoryUpdateFact | Memory | Write | Yes (you must confirm the update) |
| WebSearch | Web | Read | No (triggered by your question) |
| BrowserNavigate | Web | Read | No (triggered by your question) |
| FileRead | Files | Read | Yes (you ask it to read a file) |
| FileList | Files | Read | Yes (you ask it to list a folder) |
| SystemExecute | System | Read | Yes (allowlisted commands only) |
| ScreenCapture | Screen | Read | Yes (you ask it to look) |
| GetActiveWindow | Screen | Read | No (lightweight info check) |

---

*Every tool call is written to the audit log at `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`. Nothing happens in the dark.*
