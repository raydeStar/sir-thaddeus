# Modern Workbench UX research

**Status:** desk research and interactive prototype complete; scenario testing
with the product owner remains the decision gate before production work.

**Verdict:** pursue a **workspace-backed conversation shell with an optional
right-side workbench**. This combines the strongest parts of the three concepts
below without turning Sir Thaddeus into a clone of another assistant. The first
implementation wedge should unify recent work and contextual progress. The
durable workbench should follow as a narrow Wiki-backed surface.

This is a product-quality project. It does not change model capacity, harness
capability, benchmark scoring, or any production assistant behavior.

## Why now

The harness has made multi-step work credible. The interface should now make
that capability legible:

- where the user is working;
- what Thaddeus is doing;
- what information and permissions it is using;
- what changed;
- where the finished output lives;
- how to resume or revise the work later.

The current UI is already visually polished. Source cards, restrained typography,
the composer, local-runtime state, and the compact signet are strong. The gap is
not color or corner radius. It is the relationship between conversations,
knowledge, actions, and durable outputs.

## Current-state audit

### What should be preserved

- A calm, dark local-first visual language rather than a dashboard full of
  bright status widgets.
- The 720 px reading measure in chat.
- The composer as the primary action.
- Source cards, tool activity, memory-recall evidence, and final-state receipts.
- Explicit permission scope and the distinction between once, session, and
  persistent grants.
- Desktop/headless parity and the existing runtime state model.
- Wiki as a real durable store rather than pretending every response is an
  artifact.

### Friction found in the current information architecture

1. **Chat and History overlap.** Both are conversation lists, while Home also
   contains recents. A user must learn three places for one object.
2. **Ten top-level destinations flatten the product.** Wiki, Activity, Data,
   Memory, Routines, Settings, and Diagnostics all receive equal navigation
   weight even though they serve very different frequencies and audiences.
3. **Global permission modals remove context.** The modal is careful and
   technically clear, but it covers the conversation that explains why the
   action is needed.
4. **Finished work remains embedded in chat.** A useful brief, plan, or Wiki
   draft does not become a stable object with a title, revision state, and
   return path.
5. **Progress is evidence-rich but spatially fragmented.** Tool pills, memory
   chips, sources, activity, and diagnostics are individually useful. They do
   not yet form one answer to “what is happening?”
6. **Hover expansion hides navigation labels.** It saves width, but it makes
   the primary information architecture less scannable and causes layout
   movement.

## External product patterns

These are patterns to learn from, not screens to reproduce.

### OpenAI

- ChatGPT Projects group chats, files, and project instructions into a reusable
  context hub. The important pattern is **work has a home**, not merely a chat
  ID. See [Projects in ChatGPT][openai-projects].
- ChatGPT search now spans chats, projects, images, and documents from one
  surface. The important pattern is **one retrieval path across object types**.
  See [ChatGPT release notes][openai-release-notes].
- Canvas moves substantial editable content into a right-side surface, supports
  direct edits and targeted feedback, and exposes version history. The important
  pattern is **conversation and durable output coexist**. See
  [Canvas][openai-canvas].
- Deep research proposes a plan, exposes progress, allows interruption, and
  returns a cited report. The important pattern is **long work remains
  steerable**. See [Deep research][openai-research].

### Anthropic

- Claude Projects create self-contained workspaces with chat history,
  knowledge, and project-specific instructions. See
  [Claude Projects][claude-projects].
- Claude Artifacts place substantial reusable documents, code, and interactive
  content in a dedicated pane beside the conversation. See
  [Claude Artifacts][claude-artifacts].

## Three comparable directions

### A. Conversation-first refinement

Keep chat as the only primary object. Replace Home, Chat, and History with a
single searchable Recents surface. Add inline permissions, a compact work
progress card, and a persistent composer.

**Strengths:** smallest implementation risk, immediate clarity, works with
existing APIs.

**Weaknesses:** durable outputs and workspace context remain secondary. The
product feels cleaner but not structurally more capable.

### B. Workspace-backed conversation — recommended

Introduce a lightweight workspace object that groups conversations, attached
Wiki knowledge, instructions, and outputs. The center remains conversational.
The right workbench opens only when a durable object deserves independent
attention.

**Strengths:** clear return point for ongoing work, natural home for knowledge,
fits local-first ownership, and avoids showing the entire product as ten peer
destinations.

**Weaknesses:** requires workspace metadata, migration of existing threads into
an “Unfiled” workspace, and a careful distinction between conversation memory
and workspace knowledge.

### C. Artifact-first split workbench

Use a persistent two-pane layout with conversation on the left and an editable
document, code file, Wiki page, or report on the right.

**Strengths:** strongest experience for drafting and revision; makes outputs
feel concrete.

**Weaknesses:** too heavy for quick questions, expensive on narrow screens, and
risks building a generic editor before the object model is settled.

## Decision matrix

Scores are directional, from 1 (weak) to 5 (strong).

| Criterion | A: conversation | B: workspace | C: artifact |
| --- | ---: | ---: | ---: |
| Quick-question flow | 5 | 5 | 3 |
| Resume multi-day work | 3 | 5 | 4 |
| Harness progress visibility | 4 | 5 | 5 |
| Durable output handling | 2 | 4 | 5 |
| Local-first fit | 4 | 5 | 4 |
| Mobile/narrow layout | 5 | 4 | 2 |
| Reuse of current APIs | 5 | 3 | 2 |
| Implementation risk | 5 | 3 | 2 |
| **Unweighted total** | **33** | **34** | **27** |

The totals are intentionally close. Direction B wins because it improves the
product model, not because it accumulates the most visual features. It should
borrow A's quiet default and C's workbench only when needed.

## Recommended interaction model

### Navigation

- Keep a stable labeled sidebar on desktop.
- Primary: New conversation, Search, Workspaces, and Recents.
- Secondary: Wiki/knowledge and Routines.
- Put Activity, Data, Memory, Diagnostics, and provider configuration under a
  “System” area. They remain available without competing with ordinary work.
- Collapse Chat and History into one conversation surface.

### Conversation

- Keep messages readable and calm.
- Consolidate tool activity, memory use, sources, state verification, and
  retries into one expandable **work receipt** attached to the answer.
- Show long-running work as an inline plan with current step, elapsed time,
  completed evidence, and a Stop/Steer action.
- Keep the composer docked and preserve slash commands, voice, Wiki context,
  and offline controls behind progressive disclosure.

### Permissions

- Render normal permission decisions inline at the point where work pauses.
- Show a plain-language action first, exact tool arguments second.
- Keep Deny and Allow once visually explicit.
- Keep persistent grants available but quieter; describe the scope before the
  user commits.
- Reserve a true modal only for exceptional security boundaries where the
  conversation must not remain interactive.

### Workbench

- Open on demand for a substantial, reusable object: Wiki page, report, plan,
  document, code, or structured result.
- Treat the object as authoritative; chat discusses and revises it.
- Support Preview/Edit/History, saved state, “show changes,” and return to the
  source conversation.
- Start with Wiki-backed Markdown. Do not build a generic multi-format editor
  in the first wedge.

## Prototype

Open [`docs/prototypes/modern-workbench/index.html`](../prototypes/modern-workbench/index.html)
in a browser. It is a static, dependency-free interaction prototype and does
not call the product runtime.

The top concept switcher makes all three directions comparable using the same
scenario and visual language. Direction B is selected by default. Try:

1. switching between Conversation, Workspace, and Artifact;
2. expanding the live work plan;
3. approving or denying the inline permission;
4. opening and closing the durable workbench;
5. switching Preview, Edit, and History;
6. narrowing the window to observe the stacked workbench.

The prototype deliberately reuses the product's ink, brass, midnight, source,
permission, and receipt vocabulary. It also carries forward the strongest idea
from the earlier untracked Steward's Desk exploration: the composer and work
context do not disappear while the assistant is working.

## Scenario test plan

Run each concept against the same five scenarios:

1. Ask a quick factual question and start a follow-up.
2. Ask for three issues to be investigated in one turn.
3. Reach a file-write permission interruption, inspect scope, and deny it.
4. Run a source-backed research task, steer it midway, and inspect citations.
5. Turn the result into a Wiki brief, edit one paragraph, inspect changes, and
   return the following day.

Collect:

- time to first action;
- clicks or taps;
- backtracks;
- scroll interruptions;
- wrong-surface visits;
- whether the user can answer “where am I?”, “what is it doing?”, “what will
  change?”, and “can I undo it?”;
- 1–5 ratings for calm, modern, trustworthy, and easy to resume.

Decision gate:

- no regression in the quick-question scenario versus the current product;
- at least 30% fewer wrong-surface visits across scenarios 2–5;
- every participant correctly describes permission scope before approving;
- at least 80% can resume the saved output without reopening the source chat;
- no critical keyboard, focus, contrast, or narrow-layout accessibility defect.

## Implementation sequence after validation

### Wedge 1: one place for conversations

- Merge Chat and History.
- Add global conversation search, pinning, and grouped recents.
- Keep Home as the empty/new-work state of that same surface.
- Stabilize the labeled desktop sidebar.

### Wedge 2: contextual work state

- Replace routine permission modals with an inline pause card.
- Create one expandable work receipt for tools, memory, sources, verification,
  and retry history.
- Add a steerable progress card for multi-step work.

### Wedge 3: lightweight workspaces

- Add workspace metadata and an Unfiled migration.
- Group threads, Wiki context, and instructions.
- Search across conversation titles, Wiki pages, and durable outputs.

### Wedge 4: Wiki-backed workbench

- Open a Wiki page beside chat.
- Add Preview/Edit/History and saved state.
- Support targeted “revise this selection” requests.

Each wedge should ship independently with focused accessibility and E2E tests.
Do not combine this research branch with assistant-pipeline experiments.

## Explicit non-goals

- Copying ChatGPT or Claude branding.
- Adding a model picker to every composer.
- Replacing Wiki with a proprietary artifact store.
- Making diagnostics invisible.
- Hiding permission scope to reduce friction.
- Building collaborative cloud projects before the local single-user model is
  excellent.
- Treating an attractive prototype as proof of usability.

[openai-projects]: https://help.openai.com/en/articles/10169521-projects-in-chatgpt
[openai-release-notes]: https://help.openai.com/en/articles/6825453-chatgpt-release-notes
[openai-canvas]: https://help.openai.com/en/articles/9930697
[openai-research]: https://help.openai.com/en/articles/10500283-deep-research-in-chatgpt
[claude-projects]: https://support.anthropic.com/en/articles/9517075-what-are-projects
[claude-artifacts]: https://support.anthropic.com/en/articles/9487310-what-are-artifacts-and-how-do-i-use-them
