import { useMemo, useState } from 'react';
import { Link } from '@tanstack/react-router';
import {
  AlertCircle,
  BookOpen,
  Check,
  ChevronDown,
  Clock3,
  FileText,
  Globe,
  HardDrive,
  History,
  MessageSquare,
  ShieldCheck,
  ThumbsDown,
  ThumbsUp,
  Undo2,
  Wrench,
} from 'lucide-react';
import type { ChatMessageSource } from '@thaddeus/shared-types';
import { SourceCards } from './SourceCards';
import { ProvenanceChip } from './ProvenanceChip';
import { useToolActivityStore } from '../stores/toolActivityStore';
import { useMemoryRecallStore } from '../stores/memoryRecallStore';
import { usePermissionsStore } from '../stores/permissionsStore';
import { useWorkbenchStore } from '../stores/workbenchStore';
import {
  deleteWikiPage,
  getWikiPage,
  listWikiRevisions,
  restoreWikiPage,
  restoreWikiRevision,
} from '../lib/wikiApi';
import type { ToolActivity } from '../stores/toolActivityStore';
import { recordAssistantOutcomeFeedback } from '../lib/activityApi';

export interface WorkReceiptProps {
  messageId: string;
  threadId?: string;
  text: string;
  sources?: ChatMessageSource[] | null;
  onRetry?: () => void;
  retryDisabled?: boolean;
}

export function WorkReceipt({
  messageId,
  threadId,
  text,
  sources,
  onRetry,
  retryDisabled,
}: WorkReceiptProps) {
  const activities = useToolActivityStore((state) => state.byMessage[messageId]) ?? EMPTY_ACTIVITIES;
  const memory = useMemoryRecallStore((state) => state.byMessage[messageId]);
  const allResolved = usePermissionsStore((state) => state.resolved);
  const openWikiPage = useWorkbenchStore((state) => state.openWikiPage);
  const [expanded, setExpanded] = useState(false);
  const [effectAction, setEffectAction] = useState<EffectActionState>({ phase: 'ready' });
  const [outcomeFeedback, setOutcomeFeedback] = useState<'success' | 'correction' | null>(null);

  const permissions = useMemo(
    () => allResolved.filter(({ request }) => request.turnId === messageId),
    [allResolved, messageId],
  );
  const hasErrors = activities.some((activity) => activity.status === 'error');
  const stillRunning = activities.some((activity) => activity.status === 'running');
  const webBoundary = Boolean(
    sources?.length
    || activities.some((activity) => activity.group === 'Web'),
  );
  const wikiDestination = useMemo(() => findWikiDestination(activities), [activities]);
  const reversibleWikiEffect = useMemo(
    () => [...activities].reverse().find(isReversibleWikiEffect) ?? null,
    [activities],
  );
  const durationMs = activities.reduce((total, activity) => total + (activity.durationMs ?? 0), 0);
  // Governs what the receipt *says*, never whether it appears. A turn that ran
  // no tools is the case the reader should trust least, so staying silent
  // exactly there inverted the trust signal: well-evidenced answers advertised
  // their sources while unevidenced ones passed without comment.
  const hasEvidence = activities.length > 0 || Boolean(memory) || Boolean(sources?.length) || permissions.length > 0;
  const outcome = summarizeOutcome(text);
  // Evidence *tier*, not a measured confidence. The numeric prior below is a
  // fixed per-tier weight that exists only so the local calibration metric has
  // a stable expectation to compare user-confirmed outcomes against. It is
  // deliberately never rendered as a percentage: showing "95%" would present a
  // lookup-table constant as if it were a measurement, which is exactly the
  // overtrust failure the trust surface is supposed to prevent.
  const evidenceConfidence = useMemo(() => {
    if (activities.some((activity) => activity.effectOutcome?.independentlyVerified)) {
      return { value: 0.95, label: 'Independently verified', detail: 'Runtime re-read the resulting state.' };
    }
    if (sources?.length) return { value: 0.8, label: 'Source-backed', detail: 'Cited sources are attached below.' };
    if (activities.some((activity) => activity.effectOutcome)) {
      return { value: 0.6, label: 'Tool-result only', detail: 'Reported by the tool, not re-verified.' };
    }
    return { value: 0.5, label: 'Unverified', detail: 'No tool evidence backs this response.' };
  }, [activities, sources]);

  async function submitOutcomeFeedback(success: boolean) {
    await recordAssistantOutcomeFeedback({
      messageId,
      success,
      confidence: evidenceConfidence.value,
      evidenceLevel: evidenceConfidence.label,
    });
    setOutcomeFeedback(success ? 'success' : 'correction');
  }

  async function undoEffect() {
    if (!reversibleWikiEffect || effectAction.phase === 'working') return;
    const pageId = reversibleWikiEffect.effectOutcome?.resolvedTarget || reversibleWikiEffect.effect?.target;
    if (!pageId) return;
    setEffectAction({ phase: 'working' });
    try {
      const strategy = reversibleWikiEffect.effect?.undoStrategy;
      if (strategy === 'wiki-soft-delete') {
        await deleteWikiPage(pageId);
        setEffectAction({ phase: 'undone', redo: { kind: 'restore-page', pageId } });
      } else if (strategy === 'wiki-restore') {
        await restoreWikiPage(pageId);
        setEffectAction({ phase: 'undone', redo: { kind: 'delete-page', pageId } });
      } else {
        const current = await getWikiPage(pageId);
        const revisions = await listWikiRevisions(pageId);
        const currentRevision = revisions.find((revision) => revision.version === current.page.version);
        const previous = revisions
          .filter((revision) => revision.version < current.page.version)
          .sort((left, right) => right.version - left.version)[0];
        if (!previous || !currentRevision) throw new Error('No earlier Wiki revision is available.');
        const restored = await restoreWikiRevision(pageId, previous.id, current.page.version);
        setEffectAction({
          phase: 'undone',
          redo: {
            kind: 'restore-revision',
            pageId,
            revisionId: currentRevision.id,
            expectedVersion: restored.page.version,
          },
        });
      }
    } catch (reason) {
      setEffectAction({
        phase: 'error',
        message: (reason as Error).message || 'Could not undo this effect.',
      });
    }
  }

  async function redoEffect() {
    if (effectAction.phase !== 'undone' || !effectAction.redo) return;
    setEffectAction({ phase: 'working' });
    try {
      if (effectAction.redo.kind === 'restore-page') {
        await restoreWikiPage(effectAction.redo.pageId);
      } else if (effectAction.redo.kind === 'delete-page') {
        await deleteWikiPage(effectAction.redo.pageId);
      } else {
        await restoreWikiRevision(
          effectAction.redo.pageId,
          effectAction.redo.revisionId,
          effectAction.redo.expectedVersion,
        );
      }
      setEffectAction({ phase: 'ready' });
    } catch (reason) {
      setEffectAction({
        phase: 'error',
        message: (reason as Error).message || 'Could not redo this effect.',
      });
    }
  }

  return (
    <section
      role="group"
      aria-label={`Work receipt: ${outcome}`}
      tabIndex={0}
      onKeyDown={(event) => {
        if ((event.key === 'r' || event.key === 'R') && onRetry && !retryDisabled) {
          event.preventDefault();
          onRetry();
        } else if ((event.key === 'u' || event.key === 'U') && reversibleWikiEffect) {
          event.preventDefault();
          if (effectAction.phase === 'undone') void redoEffect();
          else void undoEffect();
        }
      }}
      className="work-receipt mt-4"
      data-testid={`work-receipt-${messageId}`}
    >
      <button
        type="button"
        onClick={() => setExpanded((value) => !value)}
        aria-expanded={expanded}
        className="flex min-h-11 w-full items-start gap-3 px-3.5 py-3 text-left"
      >
        <span
          className={`mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-full ${
            hasErrors
              ? 'bg-rose-500/10 text-rose-500'
              : stillRunning
                ? 'bg-accent-soft text-accent'
                : hasEvidence
                  ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-300'
                  // No tools ran: a green tick would imply a verification that
                  // never happened, so this state stays deliberately neutral.
                  : 'bg-canvas-sunken text-ink-subtle'
          }`}
          aria-hidden
        >
          {hasErrors
            ? <AlertCircle className="h-3.5 w-3.5" />
            : hasEvidence
              ? <Check className="h-3.5 w-3.5" />
              : <MessageSquare className="h-3.5 w-3.5" />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block text-xs font-semibold text-ink">
            {hasErrors
              ? 'Completed with a blocked step'
              : stillRunning
                ? 'Work in progress'
                : hasEvidence
                  ? 'Work completed'
                  : 'Answered without tools'}
          </span>
          <span className="mt-0.5 block truncate text-[11px] text-ink-muted">{outcome}</span>
        </span>
        <span className="mt-1 flex shrink-0 items-center gap-1.5 text-[10px] text-ink-subtle">
          {durationMs > 0 ? formatDuration(durationMs) : null}
          <ChevronDown className={`h-3.5 w-3.5 transition-transform ${expanded ? 'rotate-180' : ''}`} />
        </span>
      </button>

      <div className="flex flex-wrap gap-1.5 border-t border-line px-3.5 py-2.5" aria-label="Provenance">
        {activities.slice(0, 4).map((activity) => (
          <ProvenanceChip
            key={activity.activityId}
            label={humanizeTool(activity.tool)}
            icon={<Wrench className="h-3 w-3" aria-hidden />}
            title={activity.tool}
            snippet={activity.resultSnippet ?? activity.error ?? null}
            scope={activity.effect ? `${activity.effect.capability} · ${activity.effect.boundary}` : null}
            timestamp={activity.durationMs != null ? formatDuration(activity.durationMs) : null}
            outbound={activity.group === 'Web'}
            onOpen={() => setExpanded(true)}
          />
        ))}
        {/* Never truncate provenance silently — a receipt that hides tools it
            actually used reads as narrower than the work really was. */}
        {activities.length > 4 ? (
          <button
            type="button"
            onClick={() => setExpanded(true)}
            className="receipt-chip hover:border-line-strong hover:text-ink"
            aria-label={`Show ${activities.length - 4} more tools used`}
          >
            +{activities.length - 4} more {activities.length - 4 === 1 ? 'tool' : 'tools'}
          </button>
        ) : null}
        {memory ? (
          <Link to="/memory" className="receipt-chip hover:border-line-strong hover:text-ink">
            <BookOpen className="h-3 w-3" aria-hidden />
            Memory - {memory.factsCount + memory.eventsCount + memory.chunksCount + memory.nuggetsCount}
          </Link>
        ) : null}
        {sources?.slice(0, 3).map((source, index) => (
          <ProvenanceChip
            key={`${source.url}-${index}`}
            label={source.domain || safeDomain(source.url)}
            icon={<Globe className="h-3 w-3" aria-hidden />}
            className="receipt-chip--source"
            title={source.title || source.domain || safeDomain(source.url)}
            snippet={source.excerpt ?? null}
            scope={source.url}
            outbound
            onOpen={() => window.open(source.url, '_blank', 'noopener,noreferrer')}
          />
        ))}
        {sources && sources.length > 3 ? <span className="receipt-chip">+{sources.length - 3} sources</span> : null}
        <span className={`receipt-chip ${webBoundary ? 'receipt-chip--outbound' : 'receipt-chip--local'}`}>
          {webBoundary ? <Globe className="h-3 w-3" /> : <HardDrive className="h-3 w-3" />}
          {webBoundary ? 'Web used' : 'Local'}
        </span>
        {/* Say it in the collapsed row, not just behind a disclosure: with no
            tool evidence the answer rests on the model's weights alone. */}
        {!hasEvidence && !stillRunning ? (
          <span className="receipt-chip" title="No tool, file, or source backed this answer.">
            Model only · unverified
          </span>
        ) : null}
      </div>

      {expanded ? (
        <div className="border-t border-line px-3.5 py-3 text-xs text-ink-muted">
          {activities.length > 0 ? (
            <ol className="space-y-2" aria-label="Completed actions">
              {activities.map((activity) => (
                <li key={activity.activityId} className="flex items-start gap-2">
                  {activity.status === 'error'
                    ? <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-rose-500" />
                    : <Check className="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-600 dark:text-emerald-300" />}
                  <span className="min-w-0 flex-1">
                    <span className="font-medium text-ink">{humanizeTool(activity.tool)}</span>
                    {activity.resultSnippet ? (
                      <span className="mt-0.5 block truncate text-[11px]">{activity.resultSnippet}</span>
                    ) : activity.error ? (
                      <span className="mt-0.5 block text-[11px] text-rose-500">{activity.error}</span>
                    ) : null}
                    {activity.effect ? (
                      <span className="mt-1 flex flex-wrap items-center gap-1.5 text-[10px]">
                        <span className={`receipt-chip ${activity.effect.mutating ? 'receipt-chip--outbound' : 'receipt-chip--local'}`}>
                          {activity.effect.mutating ? `${activity.effect.kind} effect` : 'read only'}
                        </span>
                        <span className="receipt-chip">{activity.effect.boundary}</span>
                        {activity.effectOutcome?.independentlyVerified ? (
                          <span className="receipt-chip receipt-chip--local">Verified state</span>
                        ) : activity.effectOutcome ? (
                          <span className="receipt-chip">Tool result only</span>
                        ) : null}
                        {activity.effectOutcome?.resolvedTarget ? (
                          <span className="max-w-full truncate text-ink-subtle">
                            {activity.effectOutcome.resolvedTarget}
                          </span>
                        ) : null}
                      </span>
                    ) : null}
                  </span>
                  {activity.durationMs != null ? (
                    <span className="shrink-0 text-[10px] text-ink-subtle">{formatDuration(activity.durationMs)}</span>
                  ) : null}
                </li>
              ))}
            </ol>
          ) : (
            <p>No external tool action was required for this response.</p>
          )}

          {permissions.length > 0 ? (
            <div className="mt-3 flex flex-wrap gap-1.5">
              {permissions.map(({ request, decision, scope }) => (
                <span key={request.id} className="receipt-chip">
                  <ShieldCheck className="h-3 w-3" />
                  {humanizeDecision(decision)} - {scope}
                </span>
              ))}
            </div>
          ) : null}

          {sources && sources.length > 0 ? <SourceCards sources={sources} /> : null}

          <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-line pt-3">
            <span className="receipt-chip" title={evidenceConfidence.detail}>
              Evidence: {evidenceConfidence.label}
            </span>
            <span className="text-[10px] text-ink-subtle">Outcome accurate?</span>
            <button
              type="button"
              className={`btn-ghost min-h-9 text-xs ${outcomeFeedback === 'success' ? 'text-emerald-600' : ''}`}
              aria-label="Mark outcome accurate"
              aria-pressed={outcomeFeedback === 'success'}
              onClick={() => { void submitOutcomeFeedback(true); }}
            >
              <ThumbsUp className="h-3.5 w-3.5" />
              Yes
            </button>
            <button
              type="button"
              className={`btn-ghost min-h-9 text-xs ${outcomeFeedback === 'correction' ? 'text-amber-600' : ''}`}
              aria-label="Mark outcome needs correction"
              aria-pressed={outcomeFeedback === 'correction'}
              onClick={() => { void submitOutcomeFeedback(false); }}
            >
              <ThumbsDown className="h-3.5 w-3.5" />
              Needs correction
            </button>
            {wikiDestination?.pageId ? (
              <button
                type="button"
                className="btn-quiet min-h-9 text-xs"
                onClick={() => openWikiPage(wikiDestination.pageId, threadId)}
              >
                <FileText className="h-3.5 w-3.5" />
                Open {wikiDestination.title || 'Wiki page'} in workbench
              </button>
            ) : null}
            <Link
              to="/settings"
              search={{ tab: 'logs', messageId }}
              className="btn-ghost min-h-9 text-xs"
            >
              <History className="h-3.5 w-3.5" />
              View audit trail
            </Link>
            {reversibleWikiEffect ? (
              <button
                type="button"
                className="btn-ghost min-h-9 text-xs"
                disabled={effectAction.phase === 'working'}
                onClick={() => {
                  if (effectAction.phase === 'undone') void redoEffect();
                  else void undoEffect();
                }}
                aria-label={effectAction.phase === 'undone' ? 'Redo Wiki effect' : 'Undo Wiki effect'}
              >
                <Undo2 className={`h-3.5 w-3.5 ${effectAction.phase === 'undone' ? 'rotate-180' : ''}`} />
                {effectAction.phase === 'working'
                  ? 'Applying...'
                  : effectAction.phase === 'undone'
                    ? 'Redo'
                    : 'Undo'}
                <kbd className="text-[9px] opacity-60">U</kbd>
              </button>
            ) : null}
            <span className="ml-auto inline-flex items-center gap-1 text-[10px] text-ink-subtle">
              <Clock3 className="h-3 w-3" />
              {durationMs > 0 ? formatDuration(durationMs) : 'No tool latency'}
            </span>
          </div>
          {effectAction.phase === 'error' ? (
            <p className="mt-2 text-[11px] text-rose-500" role="alert">{effectAction.message}</p>
          ) : effectAction.phase === 'undone' ? (
            <p className="mt-2 text-[11px] text-emerald-600 dark:text-emerald-300" role="status">
              Wiki effect undone. The prior state remains available for redo.
            </p>
          ) : null}
        </div>
      ) : null}

    </section>
  );
}

const EMPTY_ACTIVITIES: ReturnType<typeof useToolActivityStore.getState>['byMessage'][string] = [];

type RedoAction =
  | { kind: 'restore-page'; pageId: string }
  | { kind: 'delete-page'; pageId: string }
  | {
      kind: 'restore-revision';
      pageId: string;
      revisionId: string;
      expectedVersion: number;
    };

type EffectActionState =
  | { phase: 'ready' | 'working' }
  | { phase: 'undone'; redo: RedoAction }
  | { phase: 'error'; message: string };

function isReversibleWikiEffect(activity: ToolActivity): boolean {
  const target = activity.effectOutcome?.resolvedTarget || activity.effect?.target;
  return Boolean(
    target
    && activity.status === 'ok'
    && activity.effect?.capability.toLowerCase() === 'wikiwrite'
    && activity.effect.reversible
    && activity.effect.undoStrategy,
  );
}

function summarizeOutcome(text: string): string {
  const plain = text
    .replace(/```[\s\S]*?```/g, ' code ')
    .replace(/[#*_>`[\]()]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
  if (!plain) return 'Assistant response';
  const firstSentence = plain.match(/^.{1,140}?(?:[.!?](?:\s|$)|$)/)?.[0]?.trim() || plain;
  return firstSentence.length > 140 ? `${firstSentence.slice(0, 137)}...` : firstSentence;
}

function humanizeTool(tool: string): string {
  return tool
    .replace(/[_-]+/g, ' ')
    .replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function humanizeDecision(decision: string): string {
  if (decision === 'once') return 'Allowed this time';
  if (decision === 'session') return 'Allowed for session';
  if (decision === 'always') return 'Always allowed';
  return 'Denied';
}

function safeDomain(url: string): string {
  try {
    return new URL(url).hostname.replace(/^www\./, '');
  } catch {
    return 'source';
  }
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
}

function findWikiDestination(activities: typeof EMPTY_ACTIVITIES): { pageId: string; title?: string } | null {
  for (const activity of [...activities].reverse()) {
    if (!activity.tool.toLowerCase().startsWith('wiki_')) continue;
    const resolvedTarget = activity.effectOutcome?.resolvedTarget;
    if (resolvedTarget && activity.tool.toLowerCase().includes('page')) {
      return { pageId: resolvedTarget };
    }
    if (!activity.resultSnippet) continue;
    try {
      const parsed = JSON.parse(activity.resultSnippet) as unknown;
      const found = findPageIdentity(parsed);
      if (found) return found;
    } catch {
      const pageId = /"pageId"\s*:\s*"([^"]+)"/i.exec(activity.resultSnippet)?.[1];
      const title = /"title"\s*:\s*"([^"]+)"/i.exec(activity.resultSnippet)?.[1];
      if (pageId) return { pageId, title };
    }
  }
  return null;
}

function findPageIdentity(value: unknown): { pageId: string; title?: string } | null {
  if (!value || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const pageId =
    typeof record.pageId === 'string'
      ? record.pageId
      : record.page && typeof record.page === 'object' && typeof (record.page as Record<string, unknown>).id === 'string'
        ? String((record.page as Record<string, unknown>).id)
        : null;
  if (pageId) {
    const title =
      typeof record.title === 'string'
        ? record.title
        : record.page && typeof record.page === 'object' && typeof (record.page as Record<string, unknown>).title === 'string'
          ? String((record.page as Record<string, unknown>).title)
          : undefined;
    return { pageId, title };
  }
  for (const nested of Object.values(record)) {
    const found = findPageIdentity(nested);
    if (found) return found;
  }
  return null;
}
