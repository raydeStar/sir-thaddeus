import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { createMemo, deleteMemo, listMemos, updateMemo } from '../lib/memoryApi';
import type { Memo } from '@thaddeus/shared-types';

export const Route = createFileRoute('/memory')({
  component: MemoryRoute,
});

const inputCls =
  'block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink placeholder:text-ink-subtle shadow-soft focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/15';

function MemoryRoute() {
  const [memos, setMemos] = useState<Memo[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [draftTitle, setDraftTitle] = useState('');
  const [draftBody, setDraftBody] = useState('');
  const [draftTags, setDraftTags] = useState('');

  const load = async () => {
    try {
      setMemos(await listMemos());
    } catch (e) {
      setError((e as Error).message);
    }
  };
  useEffect(() => {
    void load();
  }, []);

  const onCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const tags = draftTags
        .split(',')
        .map((t) => t.trim())
        .filter(Boolean);
      await createMemo({ title: draftTitle, body: draftBody, tags, pinned: false });
      setDraftTitle('');
      setDraftBody('');
      setDraftTags('');
      await load();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const onTogglePin = async (memo: Memo) => {
    try {
      await updateMemo(memo.id, { pinned: !memo.pinned });
      await load();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  const onDelete = async (memo: Memo) => {
    try {
      await deleteMemo(memo.id);
      await load();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <PageScaffold
      testId="route-memory"
      title="Memory"
      subtitle="Saved facts, preferences, and context. You stay in control."
    >
      <form
        onSubmit={onCreate}
        data-testid="memo-create-form"
        className="mb-10 space-y-3 rounded-2xl border border-line bg-canvas-raised p-5"
      >
        <input
          type="text"
          data-testid="memo-create-title"
          placeholder="Title"
          value={draftTitle}
          onChange={(e) => setDraftTitle(e.target.value)}
          className={inputCls}
        />
        <textarea
          data-testid="memo-create-body"
          placeholder="Body (markdown)"
          value={draftBody}
          onChange={(e) => setDraftBody(e.target.value)}
          rows={3}
          className={inputCls}
        />
        <input
          type="text"
          data-testid="memo-create-tags"
          placeholder="Comma-separated tags"
          value={draftTags}
          onChange={(e) => setDraftTags(e.target.value)}
          className={inputCls}
        />
        <button
          type="submit"
          data-testid="memo-create-submit"
          disabled={busy || !draftTitle.trim()}
          className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-40"
        >
          {busy ? 'Saving…' : 'Add memo'}
        </button>
      </form>

      {error ? (
        <p data-testid="memo-error" className="mb-3 text-sm text-rose-500">
          {error}
        </p>
      ) : null}

      {memos === null && !error ? (
        <p className="text-sm italic text-ink-subtle" data-testid="memo-loading">
          Loading…
        </p>
      ) : memos !== null && memos.length === 0 ? (
        <p className="text-sm text-ink-muted" data-testid="memo-empty">
          No memos yet. Add one above.
        </p>
      ) : memos !== null ? (
        <ul data-testid="memo-list" className="space-y-3">
          {memos.map((m) => (
            <li
              key={m.id}
              data-testid={`memo-item-${m.id}`}
              className="rounded-2xl border border-line bg-canvas-raised p-5"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <h3 className="text-sm font-semibold text-ink">
                    {m.pinned ? '📌 ' : ''}
                    {m.title}
                  </h3>
                  {m.tags.length > 0 ? (
                    <p className="mt-0.5 text-xs text-ink-muted">
                      {m.tags.map((t) => `#${t}`).join(' ')}
                    </p>
                  ) : null}
                </div>
                <div className="flex shrink-0 gap-2">
                  <button
                    type="button"
                    data-testid={`memo-pin-${m.id}`}
                    onClick={() => void onTogglePin(m)}
                    className="rounded-full border border-line bg-canvas-raised px-2.5 py-1 text-xs text-ink-muted transition hover:bg-accent-soft hover:text-ink"
                  >
                    {m.pinned ? 'Unpin' : 'Pin'}
                  </button>
                  <button
                    type="button"
                    data-testid={`memo-delete-${m.id}`}
                    onClick={() => void onDelete(m)}
                    className="rounded-full border border-rose-500/30 px-2.5 py-1 text-xs text-rose-500 transition hover:bg-rose-500/10"
                  >
                    Delete
                  </button>
                </div>
              </div>
              {m.body ? (
                <pre className="mt-2 whitespace-pre-wrap text-xs text-ink-muted">{m.body}</pre>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
    </PageScaffold>
  );
}
