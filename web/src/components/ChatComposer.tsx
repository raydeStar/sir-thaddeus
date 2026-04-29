import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link } from '@tanstack/react-router';
import {
  ArrowUp,
  BookOpen,
  ChevronDown,
  FileText,
  Folder,
  History,
  Library,
  Loader2,
  X,
} from 'lucide-react';
import { getWikiTree, listWikiRoots } from '../lib/wikiApi';
import type { WikiChatContextInput } from '../lib/chatApi';

export type WikiContextSelection = WikiChatContextInput;

export interface WikiContextOption {
  value: string;
  mode: 'all' | 'root' | 'folder' | 'page';
  rootId?: string;
  folderId?: string;
  pageId?: string;
  title: string;
}

export interface ChatComposerProps {
  /** Current draft text. Controlled by the caller. */
  value: string;
  onChange: (next: string) => void;
  /** Submit handler. Receives the trimmed draft and the selected wiki context. */
  onSubmit: (text: string, wikiContext?: WikiContextSelection) => void | Promise<void>;
  /** Disable the input + submit while a turn is in flight. */
  sending: boolean;
  /** Placeholder for the textarea. */
  placeholder?: string;
  /** Test id for the textarea. */
  inputTestId?: string;
  /** Test id for the send button. */
  sendTestId?: string;
  /** Optional render slot for extra toolbar items on the right (e.g. New chat link). */
  rightActions?: ReactNode;
  /** Whether to show the wiki context picker. Defaults to true. */
  showWikiContext?: boolean;
  /** Min height for the textarea, in pixels. Default 44. */
  minRows?: number;
  /** Auto-focus the input on mount. */
  autoFocus?: boolean;
}

/**
 * Sleek chat composer surface used by both the home hero and the thread view.
 *
 * Layout: a confident rounded shell containing the message field on top and a
 * compact toolbar across the bottom — wiki context picker on the left, custom
 * actions in the middle, and a gradient send button on the right.
 */
export function ChatComposer({
  value,
  onChange,
  onSubmit,
  sending,
  placeholder = 'Message Sir Thaddeus…',
  inputTestId,
  sendTestId,
  rightActions,
  showWikiContext = true,
  minRows = 44,
  autoFocus,
}: ChatComposerProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const [wikiContextValue, setWikiContextValue] = useState('');
  const [wikiContextOptions, setWikiContextOptions] = useState<WikiContextOption[]>([]);
  const [wikiContextLoading, setWikiContextLoading] = useState(false);

  const selectedWikiContextOption = useMemo(
    () => wikiContextOptions.find((o) => o.value === wikiContextValue) ?? null,
    [wikiContextOptions, wikiContextValue],
  );

  useEffect(() => {
    if (autoFocus) textareaRef.current?.focus();
  }, [autoFocus]);

  // Auto-grow the textarea so users can see what they're typing without
  // scrolling, but cap it so it never overtakes the page.
  useEffect(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 220)}px`;
  }, [value]);

  useEffect(() => {
    if (!showWikiContext) return;
    let disposed = false;
    const load = async () => {
      setWikiContextLoading(true);
      try {
        const roots = await listWikiRoots();
        const trees = await Promise.all(
          roots.map(async (root) => ({ root, tree: await getWikiTree(root.id) })),
        );
        if (disposed) return;
        setWikiContextOptions([
          ...(roots.length > 0
            ? [{ value: 'all', mode: 'all' as const, title: 'Every wiki root' }]
            : []),
          ...trees.flatMap(({ root, tree }) => [
            { value: `root:${root.id}`, mode: 'root' as const, rootId: root.id, title: root.name },
            ...tree.folders.map((folder) => ({
              value: `folder:${root.id}:${folder.id}`,
              mode: 'folder' as const,
              rootId: root.id,
              folderId: folder.id,
              title: `${root.name} / ${folder.name}`,
            })),
            ...tree.pages.map((page) => ({
              value: `page:${page.id}`,
              mode: 'page' as const,
              pageId: page.id,
              title: `${root.name} / ${page.title}`,
            })),
          ]),
        ]);
      } catch {
        if (!disposed) setWikiContextOptions([]);
      } finally {
        if (!disposed) setWikiContextLoading(false);
      }
    };
    void load();
    return () => {
      disposed = true;
    };
  }, [showWikiContext]);

  const canSend = !sending && value.trim().length > 0;

  const submit = async () => {
    if (!canSend) return;
    await onSubmit(value.trim(), selectionFor(selectedWikiContextOption));
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    void submit();
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void submit();
    }
  };

  const wikiContextLabel = selectedWikiContextOption
    ? 'Change wiki context'
    : wikiContextLoading
      ? 'Loading wiki context'
      : wikiContextOptions.length > 0
        ? 'Add wiki context'
        : 'Wiki context unavailable';

  return (
    <form onSubmit={handleSubmit} data-testid="chat-composer" className="composer-shell">
      <textarea
        ref={textareaRef}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        placeholder={placeholder}
        rows={1}
        data-testid={inputTestId}
        disabled={sending}
        style={{ minHeight: minRows }}
        className="block w-full resize-none border-0 bg-transparent px-2.5 py-2 text-[15px] leading-6 text-ink placeholder:text-ink-subtle focus:outline-none disabled:opacity-60"
      />

      <div className="flex flex-wrap items-center gap-2 px-1 pt-1.5">
        {showWikiContext ? (
          <label
            className={`relative flex min-w-0 max-w-full items-center gap-2 rounded-lg border px-2.5 py-1.5 text-xs transition-all duration-200 ${
              selectedWikiContextOption
                ? 'border-accent bg-accent-soft text-ink'
                : 'border-line bg-canvas text-ink-muted hover:border-line-strong hover:text-ink'
            } ${
              sending || wikiContextLoading || wikiContextOptions.length === 0
                ? 'cursor-not-allowed opacity-60'
                : 'cursor-pointer'
            }`}
            title={selectedWikiContextOption ? selectedWikiContextOption.title : 'Choose wiki context'}
          >
            {selectedWikiContextOption ? (
              <WikiContextGlyph mode={selectedWikiContextOption.mode} />
            ) : (
              <BookOpen className="h-3.5 w-3.5 shrink-0 text-ink-subtle" strokeWidth={1.75} aria-hidden />
            )}
            <span className="min-w-0 truncate font-medium">{wikiContextLabel}</span>
            <ChevronDown className="h-3.5 w-3.5 shrink-0 text-ink-subtle" strokeWidth={1.8} aria-hidden />
            <select
              value={wikiContextValue}
              onChange={(event) => setWikiContextValue(event.target.value)}
              disabled={sending || wikiContextLoading || wikiContextOptions.length === 0}
              aria-label="Wiki context"
              data-testid="chat-wiki-context"
              className="absolute inset-0 cursor-pointer opacity-0 disabled:cursor-not-allowed"
            >
              <option value="">No wiki context</option>
              {wikiContextOptions.map((option) => (
                <option key={option.value} value={option.value}>
                  {wikiContextOptionLabel(option)}
                </option>
              ))}
            </select>
          </label>
        ) : null}

        <div className="ml-auto flex items-center gap-1">
          {rightActions}
          <Link
            to="/history"
            className="chat-composer-icon-button"
            aria-label="Chat history"
            title="Chat history"
          >
            <History className="h-4 w-4" strokeWidth={1.8} />
          </Link>
          <button
            type="submit"
            data-testid={sendTestId}
            disabled={!canSend}
            aria-label="Send message"
            className="composer-send ml-1"
          >
            {sending ? (
              <Loader2 className="h-4 w-4 animate-spin" strokeWidth={2.1} />
            ) : (
              <ArrowUp className="h-4 w-4" strokeWidth={2.25} />
            )}
          </button>
        </div>

        {selectedWikiContextOption ? (
          <div
            className={wikiContextChipClass(selectedWikiContextOption.mode)}
            data-testid="chat-wiki-context-active"
          >
            <span className="flex min-w-0 items-center gap-1.5">
              <WikiContextGlyph mode={selectedWikiContextOption.mode} />
              <span className="shrink-0 font-medium text-ink-muted">
                {wikiContextKind(selectedWikiContextOption.mode)}
              </span>
              <span className="truncate">{selectedWikiContextOption.title}</span>
            </span>
            <button
              type="button"
              onClick={() => setWikiContextValue('')}
              className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-ink-subtle transition hover:bg-canvas-raised hover:text-ink"
              aria-label="Clear wiki context"
              title="Clear wiki context"
              disabled={sending}
            >
              <X className="h-3.5 w-3.5" strokeWidth={1.9} />
            </button>
          </div>
        ) : null}
      </div>
    </form>
  );
}

function selectionFor(option: WikiContextOption | null): WikiContextSelection | undefined {
  if (!option) return undefined;
  if (option.mode === 'all') return { mode: 'all' };
  if (option.mode === 'root' && option.rootId) return { mode: 'root', rootId: option.rootId };
  if (option.mode === 'folder' && option.rootId && option.folderId) {
    return { mode: 'folder', rootId: option.rootId, folderId: option.folderId };
  }
  if (option.mode === 'page' && option.pageId) return { mode: 'page', pageId: option.pageId };
  return undefined;
}


function wikiContextKind(mode: WikiContextOption['mode']) {
  if (mode === 'all') return 'All Roots';
  if (mode === 'root') return 'Root';
  if (mode === 'folder') return 'Folder';
  return 'Page';
}

function wikiContextOptionLabel(option: WikiContextOption) {
  if (option.mode === 'all') return 'All roots';
  return `${wikiContextKind(option.mode)} / ${option.title}`;
}

function wikiContextChipClass(mode: WikiContextOption['mode']) {
  const base =
    'flex min-w-0 basis-full items-center justify-between gap-2 rounded-lg border px-2.5 py-1.5 text-xs text-ink';
  return mode === 'all'
    ? `${base} border-amber-500/35 bg-amber-500/10`
    : `${base} border-accent/25 bg-accent-soft`;
}

function WikiContextGlyph({ mode }: { mode: WikiContextOption['mode'] }) {
  if (mode === 'all')
    return <Library className="h-3.5 w-3.5 shrink-0 text-amber-600" strokeWidth={1.8} aria-hidden />;
  if (mode === 'root')
    return <Library className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.8} aria-hidden />;
  if (mode === 'folder')
    return <Folder className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.8} aria-hidden />;
  return <FileText className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.8} aria-hidden />;
}
