import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Pencil, X } from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import { Markdown } from '../components/Markdown';
import { createMemo, deleteMemo, listMemos, updateMemo } from '../lib/memoryApi';
import type { Memo } from '@thaddeus/shared-types';

export const Route = createFileRoute('/memory')({
  component: MemoryRoute,
});

const inputCls =
  'block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink placeholder:text-ink-subtle shadow-soft focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/15';

interface DraftFields {
  title: string;
  body: string;
  tags: string;
}

function parseTags(raw: string): string[] {
  return raw
    .split(',')
    .map((t) => t.trim())
    .filter(Boolean);
}

function MemoryRoute() {
  const [memos, setMemos] = useState<Memo[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [draftTitle, setDraftTitle] = useState('');
  const [draftBody, setDraftBody] = useState('');
  const [draftTags, setDraftTags] = useState('');
  const [editingId, setEditingId] = useState<string | null>(null);
  const [edit, setEdit] = useState<DraftFields>({ title: '', body: '', tags: '' });
  const [editBusy, setEditBusy] = useState(false);

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
      await createMemo({
        title: draftTitle,
        body: draftBody,
        tags: parseTags(draftTags),
        pinned: false,
      });
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

  const beginEdit = (memo: Memo) => {
    setEditingId(memo.id);
    setEdit({
      title: memo.title,
      body: memo.body,
      tags: memo.tags.join(', '),
    });
  };

  const cancelEdit = () => {
    setEditingId(null);
    setEdit({ title: '', body: '', tags: '' });
  };

  const saveEdit = async (memo: Memo) => {
    if (editBusy) return;
    setEditBusy(true);
    setError(null);
    try {
      await updateMemo(memo.id, {
        title: edit.title.trim(),
        body: edit.body,
        tags: parseTags(edit.tags),
      });
      cancelEdit();
      await load();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setEditBusy(false);
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
          {memos.map((m) => {
            const isEditing = editingId === m.id;
            return (
              <li
                key={m.id}
                data-testid={`memo-item-${m.id}`}
                className="rounded-2xl border border-line bg-canvas-raised p-5"
              >
                {isEditing ? (
                  <div className="space-y-3">
                    <input
                      type="text"
                      data-testid={`memo-edit-title-${m.id}`}
                      value={edit.title}
                      onChange={(e) => setEdit((d) => ({ ...d, title: e.target.value }))}
                      placeholder="Title"
                      className={inputCls}
                    />
                    <textarea
                      data-testid={`memo-edit-body-${m.id}`}
                      value={edit.body}
                      onChange={(e) => setEdit((d) => ({ ...d, body: e.target.value }))}
                      placeholder="Body (markdown)"
                      rows={4}
                      className={inputCls}
                    />
                    <input
                      type="text"
                      data-testid={`memo-edit-tags-${m.id}`}
                      value={edit.tags}
                      onChange={(e) => setEdit((d) => ({ ...d, tags: e.target.value }))}
                      placeholder="Comma-separated tags"
                      className={inputCls}
                    />
                    <div className="flex gap-2">
                      <button
                        type="button"
                        data-testid={`memo-edit-save-${m.id}`}
                        onClick={() => void saveEdit(m)}
                        disabled={editBusy || !edit.title.trim()}
                        className="inline-flex items-center gap-1.5 rounded-full bg-accent px-4 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-40"
                      >
                        {editBusy ? 'Saving…' : 'Save'}
                      </button>
                      <button
                        type="button"
                        data-testid={`memo-edit-cancel-${m.id}`}
                        onClick={cancelEdit}
                        disabled={editBusy}
                        className="rounded-full border border-line px-3 py-1.5 text-sm text-ink-muted transition hover:text-ink"
                      >
                        Cancel
                      </button>
                    </div>
                  </div>
                ) : (
                  <>
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
                          data-testid={`memo-edit-${m.id}`}
                          onClick={() => beginEdit(m)}
                          aria-label="Edit memo"
                          className="inline-flex items-center gap-1 rounded-full border border-line bg-canvas-raised px-2.5 py-1 text-xs text-ink-muted transition hover:bg-accent-soft hover:text-ink"
                        >
                          <Pencil className="h-3 w-3" strokeWidth={1.75} />
                          Edit
                        </button>
                        <button
                          type="button"
                          data-testid={`memo-delete-${m.id}`}
                          onClick={() => void onDelete(m)}
                          aria-label="Delete memo"
                          className="inline-flex items-center gap-1 rounded-full border border-rose-500/30 px-2.5 py-1 text-xs text-rose-500 transition hover:bg-rose-500/10"
                        >
                          <X className="h-3 w-3" strokeWidth={1.75} />
                          Delete
                        </button>
                      </div>
                    </div>
                    {m.body ? (
                      <div className="mt-2" data-testid={`memo-body-${m.id}`}>
                        <Markdown>{m.body}</Markdown>
                      </div>
                    ) : null}
                  </>
                )}
              </li>
            );
          })}
        </ul>
      ) : null}
    </PageScaffold>
  );
}
