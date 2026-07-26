import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Calendar,
  ExternalLink,
  ListChecks,
  Loader2,
  Pencil,
  Pause,
  Pin,
  PinOff,
  Play,
  RefreshCw,
  Search,
  Sparkles,
  Trash2,
  UserCircle,
  X,
} from 'lucide-react';
import { PageScaffold } from '../components/PageScaffold';
import { getTurnTrace } from '../lib/activityApi';
import {
  deleteEvent,
  deleteFact,
  deleteNugget,
  getMemoryPolicy,
  getMemoryOverview,
  listEvents,
  listFacts,
  listNuggets,
  runReflection,
  resetMemory,
  setMemoryEnabled,
  setNuggetPinned,
  updateFact,
  updateNugget,
} from '../lib/memoryAuditApi';
import type {
  EventDto,
  FactDto,
  MemoryOverviewResponse,
  NuggetDto,
  ProfileDto,
  ReflectionReport,
} from '@thaddeus/shared-types';

export const Route = createFileRoute('/memory')({
  component: MemoryAuditRoute,
});

const DEFAULT_LIMIT = 50;
const MEMORY_INPUT_CLASSNAME = 'w-full rounded-xl border border-line bg-canvas px-3 py-2 text-sm text-ink placeholder:text-ink-subtle focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/15';
const MEMORY_PRIMARY_BUTTON_CLASSNAME = 'inline-flex items-center gap-1 rounded-full border border-accent/25 bg-accent-soft px-3 py-1.5 text-xs font-medium text-accent transition hover:border-accent/40 disabled:cursor-not-allowed disabled:opacity-45';
const MEMORY_SECONDARY_BUTTON_CLASSNAME = 'inline-flex items-center gap-1 rounded-full border border-line bg-canvas-raised px-3 py-1.5 text-xs font-medium text-ink-muted transition hover:border-line-strong hover:text-ink disabled:cursor-not-allowed disabled:opacity-45';

type NuggetDraft = {
  text: string;
  tags: string;
};

type FactDraft = {
  subject: string;
  predicate: string;
  object: string;
};

function MemoryAuditRoute() {
  const navigate = useNavigate();
  const [overview, setOverview] = useState<MemoryOverviewResponse | null>(null);
  const [nuggets, setNuggets] = useState<NuggetDto[] | null>(null);
  const [facts, setFacts] = useState<FactDto[] | null>(null);
  const [events, setEvents] = useState<EventDto[] | null>(null);
  const [filter, setFilter] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const [actingOn, setActingOn] = useState<string | null>(null);
  const [reflecting, setReflecting] = useState(false);
  const [lastReflection, setLastReflection] = useState<ReflectionReport | null>(null);
  const [editingNuggetId, setEditingNuggetId] = useState<string | null>(null);
  const [editingNugget, setEditingNugget] = useState<NuggetDraft>({ text: '', tags: '' });
  const [editingFactId, setEditingFactId] = useState<string | null>(null);
  const [editingFact, setEditingFact] = useState<FactDraft>({ subject: '', predicate: '', object: '' });
  const [openingSourceKey, setOpeningSourceKey] = useState<string | null>(null);
  const [memoryEnabled, setMemoryEnabledState] = useState<boolean | null>(null);
  const [policyBusy, setPolicyBusy] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [resetNotice, setResetNotice] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setError(null);
    try {
      const [ov, n, f, e, policy] = await Promise.all([
        getMemoryOverview(),
        listNuggets(filter || undefined, DEFAULT_LIMIT),
        listFacts(filter || undefined, DEFAULT_LIMIT),
        listEvents(filter || undefined, DEFAULT_LIMIT),
        getMemoryPolicy(),
      ]);
      setOverview(ov);
      setNuggets(n.items);
      setFacts(f.items);
      setEvents(e.items);
      setMemoryEnabledState(policy.enabled);
    } catch (err) {
      setError((err as Error).message);
    }
  }, [filter]);

  useEffect(() => {
    void reload();
  }, [reload, tick]);

  const onTogglePin = async (nugget: NuggetDto) => {
    setActingOn(nugget.id);
    try {
      await setNuggetPinned(nugget.id, !nugget.pinned);
      await reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setActingOn(null);
    }
  };

  const onDeleteNugget = async (nugget: NuggetDto) => {
    if (!confirm(`Delete this remembered note?\n\n"${nugget.text}"`)) return;
    setActingOn(nugget.id);
    try {
      await deleteNugget(nugget.id);
      await reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setActingOn(null);
    }
  };

  const onStartEditNugget = (nugget: NuggetDto) => {
    setEditingFactId(null);
    setEditingNuggetId(nugget.id);
    setEditingNugget({
      text: nugget.text,
      tags: tagsToEditorValue(nugget.tags),
    });
  };

  const onCancelEditNugget = () => {
    setEditingNuggetId(null);
    setEditingNugget({ text: '', tags: '' });
  };

  const onSaveNugget = async () => {
    if (!editingNuggetId) return;
    if (!editingNugget.text.trim()) {
      setError('Note text cannot be empty.');
      return;
    }

    setActingOn(editingNuggetId);
    setError(null);
    try {
      await updateNugget(editingNuggetId, {
        text: editingNugget.text,
        tags: editingNugget.tags,
        tagsProvided: true,
      });
      onCancelEditNugget();
      await reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setActingOn(null);
    }
  };

  const onDeleteFact = async (fact: FactDto) => {
    const preview = `${fact.subject} ${fact.predicate} ${fact.object}`;
    if (!confirm(`Delete this fact?\n\n"${preview}"`)) return;
    setActingOn(fact.id);
    try {
      await deleteFact(fact.id);
      await reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setActingOn(null);
    }
  };

  const onStartEditFact = (fact: FactDto) => {
    setEditingNuggetId(null);
    setEditingFactId(fact.id);
    setEditingFact({
      subject: fact.subject,
      predicate: fact.predicate,
      object: fact.object,
    });
  };

  const onCancelEditFact = () => {
    setEditingFactId(null);
    setEditingFact({ subject: '', predicate: '', object: '' });
  };

  const onSaveFact = async () => {
    if (!editingFactId) return;
    if (!editingFact.subject.trim() || !editingFact.predicate.trim() || !editingFact.object.trim()) {
      setError('Fact subject, predicate, and object are all required.');
      return;
    }

    setActingOn(editingFactId);
    setError(null);
    try {
      await updateFact(editingFactId, editingFact);
      onCancelEditFact();
      await reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setActingOn(null);
    }
  };

  const onDeleteEvent = async (evt: EventDto) => {
    if (!confirm(`Delete this remembered event?\n\n"${evt.title}"`)) return;
    setActingOn(evt.id);
    try {
      await deleteEvent(evt.id);
      await reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setActingOn(null);
    }
  };

  const onReflect = async () => {
    if (reflecting) return;
    setReflecting(true);
    setError(null);
    try {
      const report = await runReflection();
      setLastReflection(report);
      await reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setReflecting(false);
    }
  };

  const onToggleMemory = async () => {
    if (policyBusy || memoryEnabled === null) return;
    setPolicyBusy(true);
    setError(null);
    try {
      const policy = await setMemoryEnabled(!memoryEnabled);
      setMemoryEnabledState(policy.enabled);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setPolicyBusy(false);
    }
  };

  const onResetMemory = async () => {
    if (resetting) return;
    if (!confirm(
      'Permanently delete all durable memory, including facts, events, conversation chunks, notes, and profile cards? This cannot be undone.',
    )) return;
    setResetting(true);
    setError(null);
    setResetNotice(null);
    try {
      const result = await resetMemory();
      setLastReflection(null);
      setResetNotice(`Memory reset complete. ${result.rowsRemoved} durable rows permanently removed.`);
      await reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setResetting(false);
    }
  };

  const onOpenSourceTurn = useCallback(
    async (sourceTurnId?: string | null, sourceRef?: string | null) => {
      const key = sourceTurnId ?? sourceRef ?? null;
      if (!key) return;

      setOpeningSourceKey(key);
      setError(null);
      try {
        let threadId = parseThreadIdFromSourceRef(sourceRef);
        if (!threadId && sourceTurnId) {
          const trace = await getTurnTrace(sourceTurnId);
          threadId = findThreadIdInTrace(trace.events);
        }

        if (!threadId) {
          throw new Error('Could not resolve the source conversation for this memory.');
        }

        await navigate({
          to: '/chat/$threadId',
          params: { threadId },
          search: { focusMessageId: sourceTurnId ?? undefined },
        });
      } catch (err) {
        setError((err as Error).message);
      } finally {
        setOpeningSourceKey(null);
      }
    },
    [navigate],
  );

  const pinnedNuggets = useMemo(
    () => (nuggets ?? []).filter((n) => n.pinned),
    [nuggets],
  );
  const otherNuggets = useMemo(
    () => (nuggets ?? []).filter((n) => !n.pinned),
    [nuggets],
  );
  const empty =
    overview !== null &&
    overview.factCount === 0 &&
    overview.eventCount === 0 &&
    overview.chunkCount === 0 &&
    overview.nuggetCount === 0 &&
    overview.profile === null;

  return (
    <PageScaffold
      testId="route-memory"
      title="Memory"
      subtitle="What the assistant has learned from your conversations. Everything here is editable — pin what matters, delete anything that's wrong."
    >
      <div className="mb-4 flex flex-wrap items-center gap-3" data-testid="memory-toolbar">
        <div className="relative min-w-0 flex-1">
          <Search
            className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-ink-subtle"
            strokeWidth={1.75}
          />
          <input
            type="search"
            data-testid="memory-search"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Search facts, events, nuggets…"
            className="w-full rounded-full border border-line bg-canvas-raised py-2 pl-9 pr-3 text-sm text-ink placeholder:text-ink-subtle focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/15"
          />
        </div>
        <button
          type="button"
          data-testid="memory-refresh"
          onClick={() => setTick((n) => n + 1)}
          className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3 py-1.5 text-xs font-medium text-ink-muted transition hover:border-line-strong hover:text-ink"
        >
          <RefreshCw className="h-3.5 w-3.5" strokeWidth={1.75} />
          Refresh
        </button>
        <button
          type="button"
          data-testid="memory-reflect"
          onClick={() => void onReflect()}
          disabled={reflecting}
          title="Scan facts and merge duplicates (same subject, predicate, object)."
          className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3 py-1.5 text-xs font-medium text-ink-muted transition hover:border-line-strong hover:text-ink disabled:cursor-not-allowed disabled:opacity-50"
        >
          {reflecting ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.75} />
          ) : (
            <Sparkles className="h-3.5 w-3.5" strokeWidth={1.75} />
          )}
          {reflecting ? 'Reflecting…' : 'Tidy memory'}
        </button>
      </div>

      <div className="mb-4 flex flex-wrap items-center gap-2" aria-label="Memory controls">
        <button
          type="button"
          data-testid="memory-policy-toggle"
          onClick={() => void onToggleMemory()}
          disabled={policyBusy || memoryEnabled === null}
          aria-pressed={memoryEnabled === false}
          title={
            memoryEnabled === false
              ? 'Resume durable memory reads and writes'
              : 'Pause durable memory without deleting existing entries'
          }
          className={MEMORY_SECONDARY_BUTTON_CLASSNAME}
        >
          {policyBusy ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.75} />
          ) : memoryEnabled === false ? (
            <Play className="h-3.5 w-3.5" strokeWidth={1.75} />
          ) : (
            <Pause className="h-3.5 w-3.5" strokeWidth={1.75} />
          )}
          {memoryEnabled === false ? 'Resume memory' : 'Pause memory'}
        </button>
        <button
          type="button"
          data-testid="memory-reset"
          onClick={() => void onResetMemory()}
          disabled={resetting}
          className="inline-flex items-center gap-1 rounded-full border border-rose-500/25 bg-rose-500/5 px-3 py-1.5 text-xs font-medium text-rose-600 transition hover:border-rose-500/45 hover:bg-rose-500/10 disabled:cursor-not-allowed disabled:opacity-45 dark:text-rose-300"
        >
          {resetting ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.75} />
          ) : (
            <Trash2 className="h-3.5 w-3.5" strokeWidth={1.75} />
          )}
          Reset memory
        </button>
      </div>

      {memoryEnabled === false ? (
        <div
          className="mb-4 rounded-xl border border-amber-400/35 bg-amber-500/10 px-3.5 py-3 text-sm text-amber-800 dark:text-amber-200"
          role="status"
          data-testid="memory-paused-status"
        >
          Memory is paused. Existing entries remain visible, but chat turns will not read or write durable memory.
        </div>
      ) : null}

      {resetNotice ? (
        <p
          className="mb-4 rounded-xl border border-emerald-500/30 bg-emerald-500/10 px-3.5 py-3 text-sm text-emerald-700 dark:text-emerald-300"
          role="status"
          data-testid="memory-reset-notice"
        >
          {resetNotice}
        </p>
      ) : null}

      {lastReflection ? (
        <ReflectionResultPanel report={lastReflection} onClose={() => setLastReflection(null)} />
      ) : null}

      {error ? (
        <p data-testid="memory-error" className="mb-3 text-sm text-rose-500">
          {error}
        </p>
      ) : null}

      {overview === null && !error ? (
        <p data-testid="memory-loading" className="text-sm italic text-ink-subtle">
          Loading…
        </p>
      ) : empty ? (
        <div
          data-testid="memory-empty"
          className="rounded-2xl border border-dashed border-line bg-canvas-raised/40 px-6 py-10 text-center"
        >
          <p className="text-sm text-ink-muted">
            The assistant hasn't learned anything yet.
          </p>
          <p className="mt-1 text-xs text-ink-subtle">
            Have a conversation in <Link to="/chat" className="underline">chat</Link>
            {' '}and the things you tell the assistant will start showing up here.
          </p>
        </div>
      ) : (
        <div className="space-y-8">
          {overview ? (
            <OverviewCard overview={overview} />
          ) : null}

          {overview?.profile ? (
            <ProfileSection profile={overview.profile} />
          ) : null}

          {pinnedNuggets.length > 0 ? (
            <Section
              title="Pinned (core memory)"
              count={pinnedNuggets.length}
              icon={Pin}
              description="Pinned items get injected into every chat turn unconditionally — they're the things the assistant should never have to re-discover (your name, role, hard preferences). Keep this list short; an entry that's not earning its slot is better unpinned."
              testId="memory-section-pinned"
            >
              <ul className="space-y-2">
                {pinnedNuggets.map((n) => (
                  <NuggetRow
                    key={n.id}
                    nugget={n}
                    busy={actingOn === n.id}
                    editing={editingNuggetId === n.id}
                    draft={editingNugget}
                    onTogglePin={() => void onTogglePin(n)}
                    onStartEdit={() => onStartEditNugget(n)}
                    onDraftChange={setEditingNugget}
                    onSaveEdit={() => void onSaveNugget()}
                    onCancelEdit={onCancelEditNugget}
                    onDelete={() => void onDeleteNugget(n)}
                    onOpenSourceTurn={() => void onOpenSourceTurn(n.sourceTurnId)}
                    openingSource={openingSourceKey === (n.sourceTurnId ?? null)}
                    showCoreBadge
                  />
                ))}
              </ul>
            </Section>
          ) : null}

          {otherNuggets.length > 0 ? (
            <Section
              title="Notes"
              count={overview?.nuggetCount ?? otherNuggets.length}
              icon={ListChecks}
              description="Short, atomic things the assistant extracted from chat. Pin the ones it should hold onto."
              testId="memory-section-nuggets"
            >
              <ul className="space-y-2">
                {otherNuggets.map((n) => (
                  <NuggetRow
                    key={n.id}
                    nugget={n}
                    busy={actingOn === n.id}
                    editing={editingNuggetId === n.id}
                    draft={editingNugget}
                    onTogglePin={() => void onTogglePin(n)}
                    onStartEdit={() => onStartEditNugget(n)}
                    onDraftChange={setEditingNugget}
                    onSaveEdit={() => void onSaveNugget()}
                    onCancelEdit={onCancelEditNugget}
                    onDelete={() => void onDeleteNugget(n)}
                    onOpenSourceTurn={() => void onOpenSourceTurn(n.sourceTurnId)}
                    openingSource={openingSourceKey === (n.sourceTurnId ?? null)}
                  />
                ))}
              </ul>
            </Section>
          ) : null}

          {facts && facts.length > 0 ? (
            <Section
              title="Facts"
              count={overview?.factCount ?? facts.length}
              icon={ListChecks}
              description="Structured subject–predicate–object claims auto-extracted from your conversations."
              testId="memory-section-facts"
            >
              <ul className="space-y-2">
                {facts.map((f) => (
                  <FactRow
                    key={f.id}
                    fact={f}
                    busy={actingOn === f.id}
                    editing={editingFactId === f.id}
                    draft={editingFact}
                    onStartEdit={() => onStartEditFact(f)}
                    onDraftChange={setEditingFact}
                    onSaveEdit={() => void onSaveFact()}
                    onCancelEdit={onCancelEditFact}
                    onDelete={() => void onDeleteFact(f)}
                    onOpenSourceTurn={() => void onOpenSourceTurn(f.sourceTurnId, f.sourceRef)}
                    openingSource={openingSourceKey === (f.sourceTurnId ?? f.sourceRef ?? null)}
                  />
                ))}
              </ul>
            </Section>
          ) : null}

          {events && events.length > 0 ? (
            <Section
              title="Events"
              count={overview?.eventCount ?? events.length}
              icon={Calendar}
              description="Timestamped things the assistant noticed from your conversations."
              testId="memory-section-events"
            >
              <ul className="space-y-2">
                {events.map((e) => (
                  <EventRow
                    key={e.id}
                    evt={e}
                    busy={actingOn === e.id}
                    onDelete={() => void onDeleteEvent(e)}
                    onOpenSourceTurn={() => void onOpenSourceTurn(e.sourceTurnId, e.sourceRef)}
                    openingSource={openingSourceKey === (e.sourceTurnId ?? e.sourceRef ?? null)}
                  />
                ))}
              </ul>
            </Section>
          ) : null}
        </div>
      )}
    </PageScaffold>
  );
}

function tagsToEditorValue(tags?: string | null): string {
  if (!tags) return '';

  return tags
    .split(';')
    .map((tag) => tag.trim())
    .filter(Boolean)
    .join(', ');
}

function formatOriginLabel(origin?: string | null): string | null {
  if (!origin) return null;
  return origin.replaceAll('_', ' ');
}

function formatSourceLabel(sourceTurnId?: string | null, sourceRef?: string | null): string | null {
  if (sourceTurnId) {
    return sourceTurnId.length > 12 ? `turn ${sourceTurnId.slice(0, 8)}…` : `turn ${sourceTurnId}`;
  }

  const threadId = parseThreadIdFromSourceRef(sourceRef);
  if (threadId) {
    return threadId.length > 12 ? `conversation ${threadId.slice(0, 8)}…` : `conversation ${threadId}`;
  }

  return sourceRef ?? null;
}

function parseThreadIdFromSourceRef(sourceRef?: string | null): string | null {
  if (!sourceRef) return null;
  return sourceRef.startsWith('conv:') ? sourceRef.slice(5) : null;
}

function findThreadIdInTrace(events: Array<Record<string, unknown> | null>): string | null {
  const queue: unknown[] = [...events];
  const seen = new Set<object>();

  while (queue.length > 0) {
    const current = queue.shift();
    if (!current || typeof current !== 'object') continue;
    if (seen.has(current)) continue;
    seen.add(current);

    if (Array.isArray(current)) {
      queue.push(...current);
      continue;
    }

    const record = current as Record<string, unknown>;
    if (typeof record.threadId === 'string' && record.threadId.trim()) {
      return record.threadId;
    }

    queue.push(...Object.values(record));
  }

  return null;
}

function OverviewCard({ overview }: { overview: MemoryOverviewResponse }) {
  return (
    <div
      data-testid="memory-overview"
      className="grid grid-cols-2 gap-3 rounded-2xl border border-line bg-canvas-raised/60 p-4 sm:grid-cols-4"
    >
      <Stat label="Notes" value={overview.nuggetCount} testId="memory-stat-nuggets" />
      <Stat label="Facts" value={overview.factCount} testId="memory-stat-facts" />
      <Stat label="Events" value={overview.eventCount} testId="memory-stat-events" />
      <Stat label="Chunks" value={overview.chunkCount} testId="memory-stat-chunks" />
    </div>
  );
}

function Stat({ label, value, testId }: { label: string; value: number; testId: string }) {
  return (
    <div className="rounded-xl bg-canvas/40 px-3 py-2">
      <div className="text-[10px] font-semibold uppercase tracking-[0.12em] text-ink-subtle">{label}</div>
      <div className="mt-0.5 text-xl font-semibold text-ink" data-testid={testId}>
        {value}
      </div>
    </div>
  );
}

function ProfileSection({ profile }: { profile: ProfileDto }) {
  let parsed: Record<string, unknown> | null = null;
  try {
    parsed = profile.profileJson ? JSON.parse(profile.profileJson) : null;
  } catch {
    parsed = null;
  }
  const entries = parsed
    ? Object.entries(parsed).filter(([, v]) => v !== null && v !== undefined && v !== '')
    : [];

  return (
    <Section
      title="About you"
      icon={UserCircle}
      description="The assistant's identity card for you. Used for greetings and pronoun/name handling."
      testId="memory-section-profile"
    >
      <div className="rounded-2xl border border-line bg-canvas-raised/40 p-4">
        <div className="text-sm font-semibold text-ink" data-testid="memory-profile-name">
          {profile.displayName}
        </div>
        {entries.length > 0 ? (
          <dl className="mt-2 grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 text-xs">
            {entries.map(([k, v]) => (
              <div key={k} className="contents">
                <dt className="text-ink-subtle">{k}</dt>
                <dd className="text-ink-muted">{String(v)}</dd>
              </div>
            ))}
          </dl>
        ) : (
          <p className="mt-1 text-xs italic text-ink-subtle">
            No additional profile fields recorded yet.
          </p>
        )}
      </div>
    </Section>
  );
}

interface SectionProps {
  title: string;
  count?: number;
  icon: typeof Pin;
  description: string;
  testId: string;
  children: React.ReactNode;
}

function Section({ title, count, icon: Icon, description, testId, children }: SectionProps) {
  return (
    <section data-testid={testId}>
      <header className="mb-2 flex items-baseline gap-2">
        <Icon className="h-4 w-4 text-ink-muted" strokeWidth={1.75} />
        <h2 className="text-sm font-semibold text-ink">{title}</h2>
        {typeof count === 'number' ? (
          <span className="text-[11px] uppercase tracking-[0.08em] text-ink-subtle">
            {count}
          </span>
        ) : null}
      </header>
      <p className="mb-3 text-xs text-ink-muted">{description}</p>
      {children}
    </section>
  );
}

function NuggetRow({
  nugget,
  busy,
  editing,
  draft,
  onTogglePin,
  onStartEdit,
  onDraftChange,
  onSaveEdit,
  onCancelEdit,
  onDelete,
  onOpenSourceTurn,
  openingSource,
  showCoreBadge = false,
}: {
  nugget: NuggetDto;
  busy: boolean;
  editing: boolean;
  draft: NuggetDraft;
  onTogglePin: () => void;
  onStartEdit: () => void;
  onDraftChange: React.Dispatch<React.SetStateAction<NuggetDraft>>;
  onSaveEdit: () => void;
  onCancelEdit: () => void;
  onDelete: () => void;
  onOpenSourceTurn: () => void;
  openingSource: boolean;
  /** When true, render a small "in core" badge above the row. Set by the
   * "Pinned (core memory)" section. */
  showCoreBadge?: boolean;
}) {
  return (
    <li
      data-testid={`memory-nugget-${nugget.id}`}
      data-in-core={showCoreBadge ? 'true' : undefined}
      className="flex items-start gap-3 rounded-xl border border-line bg-canvas-raised/60 px-3 py-2.5"
    >
      <div className="min-w-0 flex-1">
        {editing ? (
          <div className="space-y-2">
            <textarea
              rows={3}
              value={draft.text}
              onChange={(e) => onDraftChange((current) => ({ ...current, text: e.target.value }))}
              className={MEMORY_INPUT_CLASSNAME}
              data-testid={`memory-nugget-edit-text-${nugget.id}`}
            />
            <input
              type="text"
              value={draft.tags}
              onChange={(e) => onDraftChange((current) => ({ ...current, tags: e.target.value }))}
              placeholder="tags, comma separated"
              className={MEMORY_INPUT_CLASSNAME}
              data-testid={`memory-nugget-edit-tags-${nugget.id}`}
            />
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={onSaveEdit}
                disabled={busy}
                className={MEMORY_PRIMARY_BUTTON_CLASSNAME}
                data-testid={`memory-nugget-save-${nugget.id}`}
              >
                {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.75} /> : null}
                Save
              </button>
              <button
                type="button"
                onClick={onCancelEdit}
                disabled={busy}
                className={MEMORY_SECONDARY_BUTTON_CLASSNAME}
                data-testid={`memory-nugget-cancel-${nugget.id}`}
              >
                <X className="h-3.5 w-3.5" strokeWidth={1.75} />
                Cancel
              </button>
            </div>
          </div>
        ) : (
          <p className="text-[13px] text-ink">{nugget.text}</p>
        )}
        <div className="mt-1 flex flex-wrap items-center gap-2 text-[10px] uppercase tracking-[0.08em] text-ink-subtle">
          {showCoreBadge ? (
            <span
              data-testid={`memory-nugget-core-badge-${nugget.id}`}
              className="rounded-full border border-accent/40 bg-accent-soft px-1.5 py-0.5 font-semibold text-accent"
            >
              in core
            </span>
          ) : null}
          {nugget.tags
            ? nugget.tags
                .split(';')
                .map((t) => t.trim())
                .filter(Boolean)
                .map((t) => <span key={t}>#{t}</span>)
            : null}
          {nugget.sensitivity !== 'low' ? (
            <span className="text-amber-600 dark:text-amber-400">{nugget.sensitivity}</span>
          ) : null}
          <span>used {nugget.useCount}×</span>
        </div>
        <MemoryProvenanceMeta
          origin={nugget.origin}
          sourceTurnId={nugget.sourceTurnId}
          opening={openingSource}
          onOpenSourceTurn={onOpenSourceTurn}
        />
      </div>
      <div className="flex shrink-0 gap-1">
        {editing ? null : (
          <button
            type="button"
            onClick={onStartEdit}
            disabled={busy}
            aria-label="Edit"
            data-testid={`memory-nugget-edit-${nugget.id}`}
            className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-transparent text-ink-muted transition hover:border-line hover:bg-canvas-sunken hover:text-ink disabled:opacity-40"
          >
            <Pencil className="h-3.5 w-3.5" strokeWidth={1.75} />
          </button>
        )}
        <button
          type="button"
          onClick={onTogglePin}
          disabled={busy || editing}
          aria-label={nugget.pinned ? 'Unpin' : 'Pin'}
          data-testid={`memory-nugget-pin-${nugget.id}`}
          className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-transparent text-ink-muted transition hover:border-line hover:bg-canvas-sunken hover:text-ink disabled:opacity-40"
        >
          {busy ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.75} />
          ) : nugget.pinned ? (
            <PinOff className="h-3.5 w-3.5" strokeWidth={1.75} />
          ) : (
            <Pin className="h-3.5 w-3.5" strokeWidth={1.75} />
          )}
        </button>
        <button
          type="button"
          onClick={onDelete}
          disabled={busy || editing}
          aria-label="Delete"
          data-testid={`memory-nugget-delete-${nugget.id}`}
          className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-transparent text-ink-muted transition hover:border-rose-500/30 hover:bg-rose-500/10 hover:text-rose-500 disabled:opacity-40"
        >
          <Trash2 className="h-3.5 w-3.5" strokeWidth={1.75} />
        </button>
      </div>
    </li>
  );
}

function FactRow({
  fact,
  busy,
  editing,
  draft,
  onStartEdit,
  onDraftChange,
  onSaveEdit,
  onCancelEdit,
  onDelete,
  onOpenSourceTurn,
  openingSource,
}: {
  fact: FactDto;
  busy: boolean;
  editing: boolean;
  draft: FactDraft;
  onStartEdit: () => void;
  onDraftChange: React.Dispatch<React.SetStateAction<FactDraft>>;
  onSaveEdit: () => void;
  onCancelEdit: () => void;
  onDelete: () => void;
  onOpenSourceTurn: () => void;
  openingSource: boolean;
}) {
  return (
    <li
      data-testid={`memory-fact-${fact.id}`}
      className="flex items-start gap-3 rounded-xl border border-line bg-canvas-raised/60 px-3 py-2.5"
    >
      <div className="min-w-0 flex-1">
        {editing ? (
          <div className="space-y-2">
            <div className="grid gap-2 sm:grid-cols-3">
              <input
                type="text"
                value={draft.subject}
                onChange={(e) => onDraftChange((current) => ({ ...current, subject: e.target.value }))}
                placeholder="subject"
                className={MEMORY_INPUT_CLASSNAME}
                data-testid={`memory-fact-edit-subject-${fact.id}`}
              />
              <input
                type="text"
                value={draft.predicate}
                onChange={(e) => onDraftChange((current) => ({ ...current, predicate: e.target.value }))}
                placeholder="predicate"
                className={MEMORY_INPUT_CLASSNAME}
                data-testid={`memory-fact-edit-predicate-${fact.id}`}
              />
              <input
                type="text"
                value={draft.object}
                onChange={(e) => onDraftChange((current) => ({ ...current, object: e.target.value }))}
                placeholder="object"
                className={MEMORY_INPUT_CLASSNAME}
                data-testid={`memory-fact-edit-object-${fact.id}`}
              />
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={onSaveEdit}
                disabled={busy}
                className={MEMORY_PRIMARY_BUTTON_CLASSNAME}
                data-testid={`memory-fact-save-${fact.id}`}
              >
                {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.75} /> : null}
                Save
              </button>
              <button
                type="button"
                onClick={onCancelEdit}
                disabled={busy}
                className={MEMORY_SECONDARY_BUTTON_CLASSNAME}
                data-testid={`memory-fact-cancel-${fact.id}`}
              >
                <X className="h-3.5 w-3.5" strokeWidth={1.75} />
                Cancel
              </button>
            </div>
          </div>
        ) : (
          <p className="text-[13px] text-ink">
            <span className="font-medium">{fact.subject}</span>{' '}
            <span className="text-ink-muted">{fact.predicate}</span>{' '}
            <span className="font-medium">{fact.object}</span>
          </p>
        )}
        <div className="mt-1 flex flex-wrap items-center gap-2 text-[10px] uppercase tracking-[0.08em] text-ink-subtle">
          <span>confidence {Math.round(fact.confidence * 100)}%</span>
          {fact.sensitivity !== 'public' ? (
            <span className="text-amber-600 dark:text-amber-400">{fact.sensitivity}</span>
          ) : null}
        </div>
        <MemoryProvenanceMeta
          origin={fact.origin}
          sourceTurnId={fact.sourceTurnId}
          sourceRef={fact.sourceRef}
          opening={openingSource}
          onOpenSourceTurn={onOpenSourceTurn}
        />
      </div>
      <div className="flex shrink-0 gap-1">
        {editing ? null : (
          <button
            type="button"
            onClick={onStartEdit}
            disabled={busy}
            aria-label="Edit"
            data-testid={`memory-fact-edit-${fact.id}`}
            className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-transparent text-ink-muted transition hover:border-line hover:bg-canvas-sunken hover:text-ink disabled:opacity-40"
          >
            <Pencil className="h-3.5 w-3.5" strokeWidth={1.75} />
          </button>
        )}
        <button
          type="button"
          onClick={onDelete}
          disabled={busy || editing}
          aria-label="Delete"
          data-testid={`memory-fact-delete-${fact.id}`}
          className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-transparent text-ink-muted transition hover:border-rose-500/30 hover:bg-rose-500/10 hover:text-rose-500 disabled:opacity-40"
        >
          {busy ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.75} />
          ) : (
            <Trash2 className="h-3.5 w-3.5" strokeWidth={1.75} />
          )}
        </button>
      </div>
    </li>
  );
}

function EventRow({
  evt,
  busy,
  onDelete,
  onOpenSourceTurn,
  openingSource,
}: {
  evt: EventDto;
  busy: boolean;
  onDelete: () => void;
  onOpenSourceTurn: () => void;
  openingSource: boolean;
}) {
  return (
    <li
      data-testid={`memory-event-${evt.id}`}
      className="flex items-start gap-3 rounded-xl border border-line bg-canvas-raised/60 px-3 py-2.5"
    >
      <div className="min-w-0 flex-1">
        <div className="flex items-baseline gap-2">
          <p className="truncate text-[13px] font-medium text-ink">{evt.title}</p>
          {evt.whenIso ? (
            <span className="shrink-0 text-[10px] uppercase tracking-[0.08em] text-ink-subtle">
              {new Date(evt.whenIso).toLocaleDateString()}
            </span>
          ) : null}
        </div>
        {evt.summary ? (
          <p className="mt-0.5 text-xs text-ink-muted">{evt.summary}</p>
        ) : null}
        <div className="mt-1 flex flex-wrap items-center gap-2 text-[10px] uppercase tracking-[0.08em] text-ink-subtle">
          <span>{evt.type}</span>
          {evt.sensitivity !== 'public' ? (
            <span className="text-amber-600 dark:text-amber-400">{evt.sensitivity}</span>
          ) : null}
        </div>
        <MemoryProvenanceMeta
          origin={evt.origin}
          sourceTurnId={evt.sourceTurnId}
          sourceRef={evt.sourceRef}
          opening={openingSource}
          onOpenSourceTurn={onOpenSourceTurn}
        />
      </div>
      <button
        type="button"
        onClick={onDelete}
        disabled={busy}
        aria-label="Delete"
        data-testid={`memory-event-delete-${evt.id}`}
        className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-transparent text-ink-muted transition hover:border-rose-500/30 hover:bg-rose-500/10 hover:text-rose-500 disabled:opacity-40"
      >
        {busy ? (
          <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.75} />
        ) : (
          <Trash2 className="h-3.5 w-3.5" strokeWidth={1.75} />
        )}
      </button>
    </li>
  );
}

function MemoryProvenanceMeta({
  origin,
  sourceTurnId,
  sourceRef,
  opening,
  onOpenSourceTurn,
}: {
  origin?: string | null;
  sourceTurnId?: string | null;
  sourceRef?: string | null;
  opening: boolean;
  onOpenSourceTurn: () => void;
}) {
  const originLabel = formatOriginLabel(origin);
  const sourceLabel = formatSourceLabel(sourceTurnId, sourceRef);
  const canOpen = Boolean(sourceTurnId || sourceRef);

  if (!originLabel && !sourceLabel && !canOpen) {
    return null;
  }

  return (
    <div className="mt-2 flex flex-wrap items-center gap-2 text-[11px] text-ink-subtle">
      {originLabel ? <span>origin: {originLabel}</span> : null}
      {sourceLabel ? <span title={sourceTurnId ?? sourceRef ?? undefined}>{sourceLabel}</span> : null}
      {canOpen ? (
        <button
          type="button"
          onClick={onOpenSourceTurn}
          disabled={opening}
          className="inline-flex items-center gap-1 rounded-full border border-line bg-canvas-raised px-2 py-1 text-[10px] font-medium uppercase tracking-[0.08em] text-ink-muted transition hover:border-line-strong hover:text-ink disabled:cursor-not-allowed disabled:opacity-45"
        >
          {opening ? (
            <Loader2 className="h-3 w-3 animate-spin" strokeWidth={1.75} />
          ) : (
            <ExternalLink className="h-3 w-3" strokeWidth={1.75} />
          )}
          Open source
        </button>
      ) : null}
    </div>
  );
}

function ReflectionResultPanel({
  report,
  onClose,
}: {
  report: ReflectionReport;
  onClose: () => void;
}) {
  const ranNothing =
    report.factsScanned === 0 && report.duplicateGroups === 0 && report.factsRemoved === 0;
  return (
    <div
      data-testid="memory-reflection-result"
      className="mb-4 rounded-2xl border border-line bg-canvas-raised/60 p-4"
    >
      <div className="mb-2 flex items-start justify-between gap-3">
        <div>
          <div className="text-sm font-semibold text-ink">Memory tidied</div>
          {report.error ? (
            <p
              data-testid="memory-reflection-error"
              className="mt-0.5 text-xs text-rose-500"
            >
              {report.error}
            </p>
          ) : ranNothing ? (
            <p className="mt-0.5 text-xs text-ink-muted">
              No duplicate facts found. Memory looked clean already.
            </p>
          ) : (
            <p className="mt-0.5 text-xs text-ink-muted">
              Scanned {report.factsScanned} {report.factsScanned === 1 ? 'fact' : 'facts'},
              found {report.duplicateGroups} duplicate{' '}
              {report.duplicateGroups === 1 ? 'group' : 'groups'}, removed{' '}
              <span
                data-testid="memory-reflection-removed-count"
                className="font-semibold text-ink"
              >
                {report.factsRemoved}
              </span>{' '}
              in {report.durationMs} ms.
            </p>
          )}
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label="Dismiss reflection result"
          data-testid="memory-reflection-close"
          className="text-[11px] uppercase tracking-[0.08em] text-ink-subtle hover:text-ink"
        >
          Dismiss
        </button>
      </div>
      {report.actions.length > 0 ? (
        <details className="text-[12px] text-ink-muted">
          <summary className="cursor-pointer select-none text-ink-subtle hover:text-ink">
            Show details ({report.actions.length}{' '}
            {report.actions.length === 1 ? 'action' : 'actions'})
          </summary>
          <ul
            className="mt-2 space-y-1.5"
            data-testid="memory-reflection-actions"
          >
            {report.actions.map((action) => (
              <li
                key={action.factId}
                className="rounded-lg border border-line/60 bg-canvas-raised/80 px-3 py-2"
              >
                <div className="text-[11px] uppercase tracking-[0.08em] text-ink-subtle">
                  {action.kind === 'deduped_fact' ? 'Removed duplicate' : action.kind}
                </div>
                <div className="mt-0.5 text-[12.5px] text-ink">
                  <span className="font-medium">{action.subject}</span>{' '}
                  <span className="text-ink-muted">{action.predicate}</span>{' '}
                  <span className="font-medium">{action.object}</span>
                </div>
                <div className="mt-0.5 truncate font-mono text-[10.5px] text-ink-subtle">
                  kept {action.keptFactId} → dropped {action.factId}
                </div>
              </li>
            ))}
          </ul>
        </details>
      ) : null}
    </div>
  );
}
