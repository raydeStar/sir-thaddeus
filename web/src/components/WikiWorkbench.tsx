import { useEffect, useRef, useState } from 'react';
import {
  Check,
  Clock3,
  ExternalLink,
  FileText,
  Loader2,
  PencilLine,
  RotateCcw,
  Save,
  X,
} from 'lucide-react';
import { Markdown } from './Markdown';
import {
  getWikiPage,
  listWikiRevisions,
  updateWikiPage,
  type WikiPageDocument,
  type WikiRevision,
} from '../lib/wikiApi';
import { useWorkbenchStore } from '../stores/workbenchStore';

type WorkbenchTab = 'preview' | 'edit' | 'history';

export function WikiWorkbench() {
  const pageId = useWorkbenchStore((state) => state.pageId);
  const sourceThreadId = useWorkbenchStore((state) => state.sourceThreadId);
  const close = useWorkbenchStore((state) => state.close);
  const [document, setDocument] = useState<WikiPageDocument | null>(null);
  const [revisions, setRevisions] = useState<WikiRevision[]>([]);
  const [draft, setDraft] = useState('');
  const [tab, setTab] = useState<WorkbenchTab>('preview');
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const previousPageIdRef = useRef<string | null>(null);

  useEffect(() => {
    if (pageId && !previousPageIdRef.current) {
      previousFocusRef.current = documentActiveElement();
    }
    if (!pageId && previousPageIdRef.current) {
      previousFocusRef.current?.focus();
      previousFocusRef.current = null;
    }
    previousPageIdRef.current = pageId;
  }, [pageId]);

  useEffect(() => {
    if (!pageId) {
      setDocument(null);
      setRevisions([]);
      setDraft('');
      setError(null);
      return;
    }

    let disposed = false;
    setLoading(true);
    setError(null);
    void Promise.all([getWikiPage(pageId), listWikiRevisions(pageId)])
      .then(([nextDocument, nextRevisions]) => {
        if (disposed) return;
        setDocument(nextDocument);
        setDraft(nextDocument.markdown);
        setRevisions(nextRevisions);
      })
      .catch((reason) => {
        if (!disposed) setError((reason as Error).message || 'Could not open this Wiki page.');
      })
      .finally(() => {
        if (!disposed) setLoading(false);
      });

    return () => {
      disposed = true;
    };
  }, [pageId]);

  if (!pageId) return null;

  const dirty = Boolean(document && draft !== document.markdown);

  async function save() {
    if (!document || !dirty || saving) return;
    setSaving(true);
    setError(null);
    try {
      const saved = await updateWikiPage(document.page.id, {
        markdown: draft,
        expectedVersion: document.page.version,
        source: 'user',
        summary: 'Edited in conversation workbench',
      });
      setDocument(saved);
      setDraft(saved.markdown);
      setRevisions(await listWikiRevisions(saved.page.id));
    } catch (reason) {
      setError((reason as Error).message || 'Could not save this Wiki page.');
    } finally {
      setSaving(false);
    }
  }

  function closeWorkbench() {
    if (dirty && !window.confirm('Close the workbench and discard unsaved edits?')) return;
    close();
  }

  return (
    <aside
      className="wiki-workbench"
      aria-label="Wiki workbench"
      data-testid="wiki-workbench"
    >
      <header className="flex h-14 shrink-0 items-center gap-3 border-b border-line px-4">
        <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-accent-soft text-accent" aria-hidden>
          <FileText className="h-4 w-4" strokeWidth={1.8} />
        </span>
        <div className="min-w-0 flex-1">
          <h2 className="truncate text-sm font-semibold text-ink">
            {document?.page.title ?? 'Opening Wiki page...'}
          </h2>
          <p className="mt-0.5 truncate text-[10px] text-ink-subtle">
            Local Wiki - {dirty ? 'unsaved changes' : 'saved locally'}
          </p>
        </div>
        <button
          type="button"
          onClick={closeWorkbench}
          className="wiki-icon-button h-8 w-8"
          aria-label="Close workbench"
        >
          <X className="h-4 w-4" />
        </button>
      </header>

      <div className="flex shrink-0 items-center gap-5 border-b border-line px-4" role="tablist" aria-label="Workbench view">
        <WorkbenchTabButton id="preview" active={tab === 'preview'} onSelect={setTab} icon={FileText}>
          Preview
        </WorkbenchTabButton>
        <WorkbenchTabButton id="edit" active={tab === 'edit'} onSelect={setTab} icon={PencilLine}>
          Edit
        </WorkbenchTabButton>
        <WorkbenchTabButton id="history" active={tab === 'history'} onSelect={setTab} icon={Clock3}>
          History
        </WorkbenchTabButton>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto px-5 py-5">
        {loading ? (
          <div className="flex items-center gap-2 text-sm text-ink-muted" role="status">
            <span className="agent-breathing-dot" />
            Opening local Wiki page...
          </div>
        ) : error ? (
          <p role="alert" className="rounded-xl border border-rose-500/30 bg-rose-500/10 p-3 text-sm text-rose-500">
            {error}
          </p>
        ) : tab === 'preview' ? (
          <div role="tabpanel" aria-label="Preview" className="prose-thaddeus">
            <Markdown>{draft}</Markdown>
          </div>
        ) : tab === 'edit' ? (
          <div role="tabpanel" aria-label="Edit" className="h-full">
            <label className="sr-only" htmlFor="wiki-workbench-editor">Wiki page markdown</label>
            <textarea
              id="wiki-workbench-editor"
              value={draft}
              onChange={(event) => setDraft(event.target.value)}
              className="min-h-[420px] w-full resize-none bg-transparent font-mono text-[13px] leading-6 text-ink outline-none"
              spellCheck
            />
          </div>
        ) : (
          <div role="tabpanel" aria-label="History" className="space-y-3">
            {revisions.length > 0 ? revisions.map((revision, index) => (
              <article key={revision.id} className="rounded-xl border border-line bg-canvas-raised p-3">
                <div className="flex items-center gap-2">
                  <strong className="text-xs text-ink">
                    {index === 0 ? 'Current' : `Version ${revision.version}`}
                  </strong>
                  <span className="text-[10px] text-ink-subtle">{formatStamp(revision.createdAt)}</span>
                </div>
                <p className="mt-1 text-xs leading-5 text-ink-muted">
                  {revision.summary || `${revision.source} edit`}
                </p>
              </article>
            )) : (
              <p className="text-sm text-ink-muted">No revision history yet.</p>
            )}
          </div>
        )}
      </div>

      <footer className="flex min-h-12 shrink-0 flex-wrap items-center gap-2 border-t border-line px-4 py-2 text-[10px] text-ink-subtle">
        <span className={`inline-flex items-center gap-1.5 ${dirty ? 'text-amber-600 dark:text-amber-300' : 'text-emerald-600 dark:text-emerald-300'}`}>
          {dirty ? <PencilLine className="h-3 w-3" /> : <Check className="h-3 w-3" />}
          {dirty ? 'Unsaved' : 'Saved locally'}
        </span>
        {sourceThreadId ? <span>from this conversation</span> : null}
        <div className="ml-auto flex items-center gap-1.5">
          <a
            href={`/wiki?pageId=${encodeURIComponent(pageId)}`}
            className="btn-ghost px-2 py-1 text-[11px]"
          >
            <ExternalLink className="h-3 w-3" />
            Full Wiki
          </a>
          {tab === 'edit' ? (
            <button
              type="button"
              className="btn-primary px-3 py-1.5 text-xs"
              onClick={() => void save()}
              disabled={!dirty || saving}
            >
              {saving ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Save className="h-3.5 w-3.5" />}
              Save
            </button>
          ) : dirty ? (
            <button type="button" className="btn-quiet px-2 py-1 text-[11px]" onClick={() => setDraft(document?.markdown ?? '')}>
              <RotateCcw className="h-3 w-3" />
              Discard
            </button>
          ) : null}
        </div>
      </footer>
    </aside>
  );
}

function WorkbenchTabButton({
  id,
  active,
  onSelect,
  icon: Icon,
  children,
}: {
  id: WorkbenchTab;
  active: boolean;
  onSelect: (tab: WorkbenchTab) => void;
  icon: typeof FileText;
  children: string;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={() => onSelect(id)}
      className={`relative inline-flex min-h-11 items-center gap-1.5 text-xs transition-colors ${
        active ? 'text-ink' : 'text-ink-muted hover:text-ink'
      }`}
    >
      <Icon className="h-3.5 w-3.5" />
      {children}
      {active ? <span className="absolute inset-x-0 -bottom-px h-0.5 bg-accent" /> : null}
    </button>
  );
}

function documentActiveElement(): HTMLElement | null {
  return typeof document !== 'undefined' && document.activeElement instanceof HTMLElement
    ? document.activeElement
    : null;
}

function formatStamp(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(date);
}
