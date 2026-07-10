import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import {
  deleteRoutine,
  getRoutine,
  updateRoutine,
} from '../lib/routinesApi';
import type { Routine } from '@thaddeus/shared-types';

export const Route = createFileRoute('/routines/$id/edit')({
  component: RoutineEditRoute,
});

interface DraftItem {
  id?: string;
  text: string;
  /** Stable per-session key so React doesn't rebind when items are reordered. */
  key: string;
}

function RoutineEditRoute() {
  const { id } = Route.useParams();
  const navigate = useNavigate();

  const [routine, setRoutine] = useState<Routine | null | undefined>(undefined);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [promptTemplate, setPromptTemplate] = useState('');
  const [enabled, setEnabled] = useState(true);
  const [items, setItems] = useState<DraftItem[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const r = await getRoutine(id);
        if (cancelled) return;
        setRoutine(r);
        setName(r.name);
        setDescription(r.description);
        setPromptTemplate(r.promptTemplate ?? '');
        setEnabled(r.enabled);
        setItems(
          r.checklistItems.map((item) => ({
            id: item.id,
            text: item.text,
            key: item.id,
          })),
        );
      } catch (e) {
        if (!cancelled) {
          setError((e as Error).message);
          setRoutine(null);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [id]);

  const addItem = () => {
    setItems((prev) => [
      ...prev,
      { text: '', key: `new_${Date.now()}_${prev.length}` },
    ]);
  };

  const removeItem = (key: string) => {
    setItems((prev) => prev.filter((i) => i.key !== key));
  };

  const updateItemText = (key: string, text: string) => {
    setItems((prev) => prev.map((i) => (i.key === key ? { ...i, text } : i)));
  };

  const onSave = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const cleanedItems = items
        .map((item, index) => ({
          id: item.id,
          text: item.text.trim(),
          sortOrder: index,
        }))
        .filter((i) => i.text.length > 0);

      const updated = await updateRoutine(id, {
        name: name.trim(),
        description: description.trim(),
        checklistItems: cleanedItems,
        promptTemplate: promptTemplate.trim(),
        enabled,
      });
      setRoutine(updated);
      void navigate({ to: '/routines' });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const onDelete = async () => {
    if (busy) return;
    if (!confirm('Delete this routine and all of its run history?')) return;
    setBusy(true);
    try {
      await deleteRoutine(id);
      void navigate({ to: '/routines' });
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };

  if (routine === undefined) {
    return (
      <PageScaffold testId="route-routine-edit" title="Loading…">
        <p className="text-sm italic text-ink-subtle" data-testid="routine-edit-loading">
          Loading…
        </p>
      </PageScaffold>
    );
  }
  if (routine === null) {
    return (
      <PageScaffold testId="route-routine-edit" title="Not found">
        <p className="text-sm text-rose-500" data-testid="routine-edit-error">
          {error ?? 'Routine not found.'}
        </p>
        <Link to="/routines" className="text-sm text-ink-muted underline hover:text-ink">
          Back to routines
        </Link>
      </PageScaffold>
    );
  }

  const inputCls =
    'block w-full rounded-xl border border-line bg-canvas-raised px-3 py-2 text-sm text-ink placeholder:text-ink-subtle focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20';

  return (
    <PageScaffold
      testId="route-routine-edit"
      title={`Edit: ${routine.name}`}
      subtitle="Tune the checklist, description, and optional prompt template."
    >
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void onSave();
        }}
        data-testid="routine-edit-form"
        className="space-y-6"
      >
        <div className="space-y-3">
          <label className="block text-[12px] font-medium text-ink-muted">
            Name
            <input
              type="text"
              data-testid="routine-edit-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className={`${inputCls} mt-1.5`}
            />
          </label>
          <label className="block text-[12px] font-medium text-ink-muted">
            Description
            <textarea
              data-testid="routine-edit-description"
              rows={2}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              className={`${inputCls} mt-1.5`}
            />
          </label>
        </div>

        <div>
          <p className="mb-2 text-[12px] font-medium text-ink-muted">Checklist</p>
          <ul className="space-y-1.5" data-testid="routine-edit-items">
            {items.map((item, index) => (
              <li
                key={item.key}
                data-testid={`routine-edit-item-${index}`}
                className="flex items-center gap-2"
              >
                <span className="w-6 text-right text-[11px] text-ink-subtle">
                  {index + 1}.
                </span>
                <input
                  type="text"
                  value={item.text}
                  onChange={(e) => updateItemText(item.key, e.target.value)}
                  placeholder={`Step ${index + 1}`}
                  className={inputCls}
                />
                <button
                  type="button"
                  data-testid={`routine-edit-remove-${index}`}
                  onClick={() => removeItem(item.key)}
                  className="rounded-full p-1.5 text-ink-muted transition hover:bg-rose-500/10 hover:text-rose-500"
                  aria-label="Remove item"
                >
                  <Trash2 className="h-4 w-4" strokeWidth={1.75} />
                </button>
              </li>
            ))}
          </ul>
          <button
            type="button"
            data-testid="routine-edit-add-item"
            onClick={addItem}
            className="mt-2 inline-flex items-center gap-1.5 rounded-full border border-line px-3 py-1.5 text-xs text-ink-muted transition hover:text-ink"
          >
            <Plus className="h-3.5 w-3.5" strokeWidth={1.75} />
            Add item
          </button>
        </div>

        <label className="block text-[12px] font-medium text-ink-muted">
          Prompt template (optional)
          <textarea
            data-testid="routine-edit-prompt"
            rows={4}
            value={promptTemplate}
            onChange={(e) => setPromptTemplate(e.target.value)}
            placeholder="What you'd hand to Sir Thaddeus when you want AI help with this routine."
            className={`${inputCls} mt-1.5 font-mono text-[13px]`}
          />
        </label>

        <label className="flex items-center gap-2 text-sm text-ink">
          <input
            type="checkbox"
            data-testid="routine-edit-enabled"
            checked={enabled}
            onChange={(e) => setEnabled(e.target.checked)}
            className="h-[14px] w-[14px] accent-accent"
          />
          Enabled
        </label>

        {error ? (
          <p data-testid="routine-edit-error" className="text-sm text-rose-500">
            {error}
          </p>
        ) : null}

        <div className="flex flex-wrap items-center gap-2">
          <button
            type="submit"
            data-testid="routine-edit-save"
            disabled={busy || !name.trim()}
            className="btn-primary"
          >
            {busy ? 'Saving…' : 'Save'}
          </button>
          <Link
            to="/routines"
            className="rounded-full border border-line px-3 py-1.5 text-sm text-ink-muted transition hover:text-ink"
          >
            Cancel
          </Link>
          <button
            type="button"
            data-testid="routine-edit-delete"
            disabled={busy}
            onClick={() => void onDelete()}
            className="ml-auto rounded-full border border-rose-500/30 px-3 py-1.5 text-sm text-rose-500 transition hover:bg-rose-500/10 disabled:opacity-50"
          >
            Delete
          </button>
        </div>
      </form>
    </PageScaffold>
  );
}
