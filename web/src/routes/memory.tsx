import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { createMemo, deleteMemo, listMemos, updateMemo } from '../lib/memoryApi';
import type { Memo } from '@thaddeus/shared-types';

export const Route = createFileRoute('/memory')({
  component: MemoryRoute,
});

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
      <form onSubmit={onCreate} data-testid="memo-create-form" className="mb-6 space-y-2 rounded-md border border-slate-200 p-3">
        <input
          type="text"
          data-testid="memo-create-title"
          placeholder="Title"
          value={draftTitle}
          onChange={(e) => setDraftTitle(e.target.value)}
          className="w-full rounded-md border border-slate-300 px-3 py-1.5 text-sm"
        />
        <textarea
          data-testid="memo-create-body"
          placeholder="Body (markdown)"
          value={draftBody}
          onChange={(e) => setDraftBody(e.target.value)}
          rows={3}
          className="w-full rounded-md border border-slate-300 px-3 py-1.5 text-sm"
        />
        <input
          type="text"
          data-testid="memo-create-tags"
          placeholder="Comma-separated tags"
          value={draftTags}
          onChange={(e) => setDraftTags(e.target.value)}
          className="w-full rounded-md border border-slate-300 px-3 py-1.5 text-sm"
        />
        <button
          type="submit"
          data-testid="memo-create-submit"
          disabled={busy || !draftTitle.trim()}
          className="rounded-md bg-thaddeus-ink px-4 py-1.5 text-sm font-medium text-white disabled:opacity-50"
        >
          {busy ? 'Saving…' : 'Add memo'}
        </button>
      </form>

      {error ? (
        <p data-testid="memo-error" className="mb-3 text-sm text-red-600">
          {error}
        </p>
      ) : null}

      {memos === null ? (
        <p className="text-sm italic text-slate-500" data-testid="memo-loading">
          Loading…
        </p>
      ) : memos.length === 0 ? (
        <p className="text-sm text-slate-500" data-testid="memo-empty">
          No memos yet. Add one above.
        </p>
      ) : (
        <ul data-testid="memo-list" className="space-y-3">
          {memos.map((m) => (
            <li key={m.id} data-testid={`memo-item-${m.id}`} className="rounded-md border border-slate-200 p-3">
              <div className="flex items-start justify-between gap-2">
                <div>
                  <h3 className="text-sm font-semibold text-thaddeus-ink">
                    {m.pinned ? '📌 ' : ''}
                    {m.title}
                  </h3>
                  {m.tags.length > 0 ? (
                    <p className="text-xs text-slate-500">{m.tags.map((t) => `#${t}`).join(' ')}</p>
                  ) : null}
                </div>
                <div className="flex gap-2">
                  <button
                    type="button"
                    data-testid={`memo-pin-${m.id}`}
                    onClick={() => void onTogglePin(m)}
                    className="rounded-md border border-slate-300 px-2 py-0.5 text-xs"
                  >
                    {m.pinned ? 'Unpin' : 'Pin'}
                  </button>
                  <button
                    type="button"
                    data-testid={`memo-delete-${m.id}`}
                    onClick={() => void onDelete(m)}
                    className="rounded-md border border-red-300 px-2 py-0.5 text-xs text-red-700"
                  >
                    Delete
                  </button>
                </div>
              </div>
              {m.body ? (
                <pre className="mt-2 whitespace-pre-wrap text-xs text-slate-700">{m.body}</pre>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </PageScaffold>
  );
}
