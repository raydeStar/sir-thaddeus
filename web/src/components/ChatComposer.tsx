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
  PencilLine,
  Search,
  ShieldCheck,
  Sparkles,
  X,
  type LucideIcon,
} from 'lucide-react';
import { getWikiTree, listWikiRoots } from '../lib/wikiApi';
import type { WikiChatContextInput, WikiMutationTargetInput } from '../lib/chatApi';
import { useWikiContextStore } from '../stores/wikiContextStore';

export type WikiContextSelection = WikiChatContextInput;
export type WikiMutationTargetSelection = WikiMutationTargetInput;

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
  onSubmit: (
    text: string,
    wikiContext?: WikiContextSelection,
    wikiMutationTarget?: WikiMutationTargetSelection,
  ) => void | Promise<void>;
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

interface PromptCommand {
  name: string;
  description: string;
  icon: LucideIcon;
  build: (input: string) => string;
}

const PROMPT_COMMANDS: PromptCommand[] = [
  {
    name: 'summarize',
    description: 'Summarize selected text or attached content.',
    icon: FileText,
    build: (input) => promptWithInput('Summarize the following clearly and briefly:', input),
  },
  {
    name: 'explain',
    description: 'Explain this in plain language.',
    icon: Sparkles,
    build: (input) => promptWithInput('Explain this clearly, with the important details first:', input),
  },
  {
    name: 'rewrite',
    description: 'Rewrite while preserving meaning.',
    icon: PencilLine,
    build: (input) => promptWithInput('Rewrite this for clarity while preserving the meaning and tone:', input),
  },
  {
    name: 'research',
    description: 'Separate facts from uncertainty.',
    icon: Search,
    build: (input) => promptWithInput('Research this and separate known facts from uncertainty:', input),
  },
];

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
  const wikiContextValue = useWikiContextStore((s) => s.value);
  const setWikiContextValue = useWikiContextStore((s) => s.setValue);
  const mutationTargetValue = useWikiContextStore((s) => s.mutationTargetValue);
  const setMutationTargetValue = useWikiContextStore((s) => s.setMutationTargetValue);
  const [wikiContextOptions, setWikiContextOptions] = useState<WikiContextOption[]>([]);
  const [wikiContextLoading, setWikiContextLoading] = useState(false);
  const [highlightedCommandIndex, setHighlightedCommandIndex] = useState(0);
  const [dismissedSlashValue, setDismissedSlashValue] = useState<string | null>(null);

  const selectedWikiContextOption = useMemo(
    () => wikiContextOptions.find((o) => o.value === wikiContextValue) ?? null,
    [wikiContextOptions, wikiContextValue],
  );
  const mutationTargetOptions = useMemo(
    () => wikiContextOptions.filter((option) => option.mode === 'root' || option.mode === 'page'),
    [wikiContextOptions],
  );
  const selectedMutationTargetOption = useMemo(
    () => mutationTargetOptions.find((option) => option.value === mutationTargetValue) ?? null,
    [mutationTargetOptions, mutationTargetValue],
  );

  const slashCommandState = useMemo(() => parseSlashCommand(value), [value]);
  const matchingPromptCommands = useMemo(() => {
    if (!slashCommandState) return [];
    return PROMPT_COMMANDS.filter((command) => command.name.includes(slashCommandState.query));
  }, [slashCommandState]);
  const slashMenuOpen = Boolean(
    slashCommandState && matchingPromptCommands.length > 0 && dismissedSlashValue !== value,
  );

  useEffect(() => {
    setHighlightedCommandIndex(0);
  }, [slashCommandState?.query]);

  useEffect(() => {
    if (highlightedCommandIndex >= matchingPromptCommands.length) {
      setHighlightedCommandIndex(Math.max(0, matchingPromptCommands.length - 1));
    }
  }, [highlightedCommandIndex, matchingPromptCommands.length]);

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

  const applyPromptCommand = (command: PromptCommand) => {
    const input = slashCommandState?.input ?? '';
    onChange(command.build(input));
    window.requestAnimationFrame(() => textareaRef.current?.focus());
  };

  const submit = async () => {
    if (!canSend) return;
    await onSubmit(
      value.trim(),
      selectionFor(selectedWikiContextOption),
      mutationTargetFor(selectedMutationTargetOption),
    );
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    void submit();
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (slashMenuOpen) {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setHighlightedCommandIndex((index) => (index + 1) % matchingPromptCommands.length);
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        setHighlightedCommandIndex((index) => (index - 1 + matchingPromptCommands.length) % matchingPromptCommands.length);
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        setDismissedSlashValue(value);
        return;
      }
      if (e.key === 'Tab' || e.key === 'Enter') {
        e.preventDefault();
        applyPromptCommand(matchingPromptCommands[highlightedCommandIndex]);
        return;
      }
    }

    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void submit();
    }
  };

  const wikiContextLabel = selectedWikiContextOption
    ? selectedWikiContextOption.title
    : wikiContextLoading
      ? 'Loading wiki context'
      : wikiContextOptions.length > 0
        ? 'Add wiki context'
        : 'Wiki context unavailable';

  return (
    <form onSubmit={handleSubmit} data-testid="chat-composer" className="composer-shell">
      {slashMenuOpen ? (
        <div
          data-testid="chat-slash-menu"
          className="absolute inset-x-0 bottom-full z-20 mb-2 max-h-72 overflow-auto rounded-2xl border border-line bg-canvas-raised p-1.5 shadow-soft"
          role="listbox"
          aria-label="Prompt commands"
        >
          {matchingPromptCommands.map((command, index) => {
            const Icon = command.icon;
            const active = index === highlightedCommandIndex;
            return (
              <button
                key={command.name}
                type="button"
                onMouseDown={(event) => event.preventDefault()}
                onMouseEnter={() => setHighlightedCommandIndex(index)}
                onClick={() => applyPromptCommand(command)}
                data-testid={`chat-slash-command-${command.name}`}
                className={`flex w-full items-center gap-3 rounded-xl px-3 py-2 text-left transition ${
                  active ? 'bg-accent-soft text-ink' : 'text-ink-muted hover:bg-canvas-sunken hover:text-ink'
                }`}
                role="option"
                aria-selected={active}
              >
                <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-line bg-canvas text-ink-muted">
                  <Icon className="h-4 w-4" strokeWidth={1.8} aria-hidden />
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block text-sm font-medium text-ink">/{command.name}</span>
                  <span className="block truncate text-xs text-ink-muted">{command.description}</span>
                </span>
              </button>
            );
          })}
        </div>
      ) : null}

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
          <div
            className={`relative flex min-w-0 max-w-full items-center gap-2 rounded-lg border px-2.5 py-1.5 text-xs transition-colors duration-150 ${
              selectedWikiContextOption
                ? 'border-line bg-canvas-sunken text-ink'
                : 'border-line bg-canvas text-ink-muted hover:border-line-strong hover:text-ink'
            } ${
              sending || wikiContextLoading || wikiContextOptions.length === 0
                ? 'cursor-not-allowed opacity-60'
                : ''
            }`}
            data-testid="chat-wiki-context-active"
            title={selectedWikiContextOption ? selectedWikiContextOption.title : 'Choose wiki context'}
          >
            {selectedWikiContextOption ? (
              <WikiContextGlyph mode={selectedWikiContextOption.mode} />
            ) : (
              <BookOpen className="h-3.5 w-3.5 shrink-0 text-ink-subtle" strokeWidth={1.75} aria-hidden />
            )}
            {selectedWikiContextOption ? (
              <span className="flex min-w-0 items-center gap-1.5">
                <span className="shrink-0 text-ink-subtle">{wikiContextKind(selectedWikiContextOption.mode)}</span>
                <span className="min-w-0 truncate font-medium text-ink">{selectedWikiContextOption.title}</span>
              </span>
            ) : (
              <span className="min-w-0 truncate font-medium">{wikiContextLabel}</span>
            )}
            {selectedWikiContextOption ? null : (
              <ChevronDown className="h-3.5 w-3.5 shrink-0 text-ink-subtle" strokeWidth={1.8} aria-hidden />
            )}
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
            {selectedWikiContextOption ? (
              <button
                type="button"
                onClick={(event) => {
                  event.preventDefault();
                  event.stopPropagation();
                  setWikiContextValue('');
                }}
                className="relative z-10 flex h-5 w-5 shrink-0 items-center justify-center rounded-full text-ink-subtle transition hover:bg-canvas-raised hover:text-ink"
                aria-label="Clear wiki context"
                title="Clear wiki context"
                disabled={sending}
              >
                <X className="h-3 w-3" strokeWidth={2} />
              </button>
            ) : null}
          </div>
        ) : null}

        {showWikiContext ? (
          <div
            className={`relative flex min-w-0 max-w-full items-center gap-2 rounded-lg border px-2.5 py-1.5 text-xs transition-colors duration-150 ${
              selectedMutationTargetOption
                ? 'border-amber-400/60 bg-amber-500/10 text-ink'
                : 'border-line bg-canvas text-ink-muted hover:border-line-strong hover:text-ink'
            } ${
              sending || wikiContextLoading || mutationTargetOptions.length === 0
                ? 'cursor-not-allowed opacity-60'
                : ''
            }`}
            data-testid="chat-wiki-mutation-target-active"
            title={
              selectedMutationTargetOption
                ? `Wiki writes limited to ${selectedMutationTargetOption.title}`
                : 'Choose an existing Wiki root or page as the write target'
            }
          >
            <ShieldCheck
              className={`h-3.5 w-3.5 shrink-0 ${selectedMutationTargetOption ? 'text-amber-600' : 'text-ink-subtle'}`}
              strokeWidth={1.8}
              aria-hidden
            />
            {selectedMutationTargetOption ? (
              <span className="flex min-w-0 items-center gap-1.5">
                <span className="shrink-0 text-ink-subtle">Write target</span>
                <span className="min-w-0 truncate font-medium text-ink">{selectedMutationTargetOption.title}</span>
              </span>
            ) : (
              <span className="min-w-0 truncate font-medium">Limit Wiki writes</span>
            )}
            {selectedMutationTargetOption ? null : (
              <ChevronDown className="h-3.5 w-3.5 shrink-0 text-ink-subtle" strokeWidth={1.8} aria-hidden />
            )}
            <select
              value={mutationTargetValue}
              onChange={(event) => setMutationTargetValue(event.target.value)}
              disabled={sending || wikiContextLoading || mutationTargetOptions.length === 0}
              aria-label="Wiki write target"
              data-testid="chat-wiki-mutation-target"
              className="absolute inset-0 cursor-pointer opacity-0 disabled:cursor-not-allowed"
            >
              <option value="">No Wiki write target</option>
              {mutationTargetOptions.map((option) => (
                <option key={option.value} value={option.value}>
                  {wikiContextOptionLabel(option)}
                </option>
              ))}
            </select>
            {selectedMutationTargetOption ? (
              <button
                type="button"
                onClick={(event) => {
                  event.preventDefault();
                  event.stopPropagation();
                  setMutationTargetValue('');
                }}
                className="relative z-10 flex h-5 w-5 shrink-0 items-center justify-center rounded-full text-ink-subtle transition hover:bg-canvas-raised hover:text-ink"
                aria-label="Clear Wiki write target"
                title="Clear Wiki write target"
                disabled={sending}
              >
                <X className="h-3 w-3" strokeWidth={2} />
              </button>
            ) : null}
          </div>
        ) : null}

        <div className="ml-auto flex items-center gap-1">
          {rightActions}
          <Link
            to="/chat"
            className="chat-composer-icon-button"
            aria-label="Conversations"
            title="Conversations"
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
      </div>
    </form>
  );
}

function parseSlashCommand(value: string): { query: string; input: string } | null {
  const match = value.match(/^\/([a-z-]*)\s*([\s\S]*)$/i);
  if (!match) return null;
  return {
    query: match[1].toLowerCase(),
    input: match[2].trimStart(),
  };
}

function promptWithInput(prefix: string, input: string): string {
  return input.trim() ? `${prefix}\n\n${input.trim()}` : `${prefix}\n\n`;
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

function mutationTargetFor(
  option: WikiContextOption | null,
): WikiMutationTargetSelection | undefined {
  if (option?.mode === 'root' && option.rootId) return { mode: 'root', rootId: option.rootId };
  if (option?.mode === 'page' && option.pageId) return { mode: 'page', pageId: option.pageId };
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

function WikiContextGlyph({ mode }: { mode: WikiContextOption['mode'] }) {
  if (mode === 'all')
    return <Library className="h-3.5 w-3.5 shrink-0 text-amber-600" strokeWidth={1.8} aria-hidden />;
  if (mode === 'root')
    return <Library className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.8} aria-hidden />;
  if (mode === 'folder')
    return <Folder className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.8} aria-hidden />;
  return <FileText className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.8} aria-hidden />;
}
