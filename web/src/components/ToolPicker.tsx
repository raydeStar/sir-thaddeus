import { useEffect, useMemo, useState } from 'react';
import { Loader2, Sparkles, Globe, FolderOpen, Monitor, Terminal, BookOpen, Pencil, type LucideIcon } from 'lucide-react';
import { listToolCatalog, suggestTools } from '../lib/automationsApi';
import type { ToolCatalogEntry } from '@thaddeus/shared-types';

interface ToolPickerProps {
  /** Current allowlist. Empty array = nothing pre-approved. */
  value: string[];
  onChange: (tools: string[]) => void;
  /** Context passed to the "Let AI pick" endpoint. */
  automationName: string;
  automationDescription?: string;
  steps: string[];
  testIdPrefix?: string;
}

const groupIcons: Record<string, LucideIcon> = {
  Safe: Sparkles,
  Web: Globe,
  Files: FolderOpen,
  System: Terminal,
  Screen: Monitor,
  MemoryRead: BookOpen,
  MemoryWrite: Pencil,
};

const groupOrder = ['Safe', 'Web', 'Files', 'System', 'Screen', 'MemoryRead', 'MemoryWrite'];

const groupLabel: Record<string, string> = {
  Safe: 'Safe (no prompts required)',
  Web: 'Web',
  Files: 'Files',
  System: 'System',
  Screen: 'Screen',
  MemoryRead: 'Memory — read',
  MemoryWrite: 'Memory — write',
};

/**
 * Tool allowlist editor for an automation. Groups tools by policy group,
 * exposes a "Let AI pick" button that asks the model to propose a minimal
 * set based on the steps, and writes the selected names through to the
 * parent via {@link ToolPickerProps.onChange}.
 *
 * Safe-group tools (time, timezone, meta) are always allowed at runtime,
 * so we show them checked + disabled to make that clear.
 */
export function ToolPicker({
  value,
  onChange,
  automationName,
  automationDescription,
  steps,
  testIdPrefix = 'automation-tools',
}: ToolPickerProps) {
  const [catalog, setCatalog] = useState<ToolCatalogEntry[] | null>(null);
  const [catalogError, setCatalogError] = useState<string | null>(null);
  const [suggesting, setSuggesting] = useState(false);
  const [suggestNote, setSuggestNote] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    listToolCatalog()
      .then((t) => { if (!cancelled) setCatalog(t); })
      .catch((e: Error) => { if (!cancelled) setCatalogError(e.message); });
    return () => { cancelled = true; };
  }, []);

  const selected = useMemo(() => new Set(value), [value]);

  const grouped = useMemo(() => {
    if (!catalog) return new Map<string, ToolCatalogEntry[]>();
    const map = new Map<string, ToolCatalogEntry[]>();
    for (const t of catalog) {
      const list = map.get(t.group) ?? [];
      list.push(t);
      map.set(t.group, list);
    }
    return map;
  }, [catalog]);

  const toggle = (name: string) => {
    if (selected.has(name)) {
      onChange(value.filter((v) => v !== name));
    } else {
      onChange([...value, name]);
    }
  };

  const toggleGroup = (tools: ToolCatalogEntry[]) => {
    const names = tools.map((t) => t.name);
    const allChecked = names.every((n) => selected.has(n));
    if (allChecked) {
      onChange(value.filter((v) => !names.includes(v)));
    } else {
      const merged = new Set(value);
      for (const n of names) merged.add(n);
      onChange([...merged]);
    }
  };

  const runSuggest = async () => {
    if (suggesting) return;
    if (steps.filter((s) => s.trim()).length === 0) {
      setSuggestNote('Add at least one step before asking the AI to pick tools.');
      return;
    }
    setSuggesting(true);
    setSuggestNote(null);
    try {
      const result = await suggestTools({
        name: automationName,
        description: automationDescription,
        steps: steps.filter((s) => s.trim()),
      });
      if (result.tools.length === 0) {
        setSuggestNote(result.note ?? 'Model did not pick any tools — choose manually.');
      } else {
        onChange(result.tools);
        setSuggestNote(
          result.note ?? `Picked ${result.tools.length} tool${result.tools.length === 1 ? '' : 's'}.`
        );
      }
    } catch (e) {
      setSuggestNote((e as Error).message);
    } finally {
      setSuggesting(false);
    }
  };

  return (
    <div className="space-y-3" data-testid={testIdPrefix}>
      <div className="flex items-center justify-between gap-3">
        <div>
          <p className="text-[13px] font-medium text-ink">
            Tools this automation can use
            {value.length > 0 ? (
              <span className="ml-2 text-[12px] font-normal text-ink-muted">
                ({value.length} selected)
              </span>
            ) : null}
          </p>
          <p className="mt-0.5 text-[12px] text-ink-muted">
            Pre-approved tools run without prompting. Anything else triggers a permission modal at runtime.
          </p>
        </div>
        <button
          type="button"
          data-testid={`${testIdPrefix}-suggest`}
          onClick={runSuggest}
          disabled={suggesting}
          className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3.5 py-1.5 text-sm font-medium text-ink transition-colors hover:bg-accent-soft disabled:opacity-50"
        >
          {suggesting ? (
            <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.75} />
          ) : (
            <Sparkles className="h-4 w-4" strokeWidth={1.75} />
          )}
          Let AI pick
        </button>
      </div>

      {suggestNote ? (
        <p className="text-[12px] text-ink-muted" data-testid={`${testIdPrefix}-suggest-note`}>
          {suggestNote}
        </p>
      ) : null}

      {catalogError ? (
        <p className="text-[12px] text-rose-500" data-testid={`${testIdPrefix}-error`}>
          Could not load tool catalog: {catalogError}
        </p>
      ) : !catalog ? (
        <p className="text-[12px] text-ink-subtle">Loading tools…</p>
      ) : catalog.length === 0 ? (
        <p className="text-[12px] text-ink-muted">
          No tools available yet. Make sure the MCP server is up in Diagnostics.
        </p>
      ) : (
        <div className="space-y-3 rounded-xl border border-line bg-canvas-raised p-3">
          {groupOrder
            .filter((g) => grouped.has(g))
            .map((group) => {
              const tools = grouped.get(group)!;
              const allChecked = tools.every((t) => selected.has(t.name));
              const someChecked = tools.some((t) => selected.has(t.name));
              const Icon = groupIcons[group] ?? Sparkles;
              const isSafe = group === 'Safe';
              return (
                <div key={group} data-testid={`${testIdPrefix}-group-${group}`}>
                  <label className="flex items-center gap-2 py-1">
                    <input
                      type="checkbox"
                      data-testid={`${testIdPrefix}-group-${group}-toggle`}
                      disabled={isSafe}
                      checked={isSafe || allChecked}
                      ref={(el) => { if (el) el.indeterminate = !allChecked && someChecked; }}
                      onChange={() => toggleGroup(tools)}
                      className="h-[14px] w-[14px] accent-accent"
                    />
                    <Icon className="h-4 w-4 text-ink-subtle" strokeWidth={1.75} />
                    <span className="text-[13px] font-medium text-ink">{groupLabel[group] ?? group}</span>
                    <span className="ml-1 text-[11px] text-ink-subtle">
                      {tools.length} tool{tools.length === 1 ? '' : 's'}
                    </span>
                  </label>
                  <div className="ml-7 grid gap-0.5 md:grid-cols-2">
                    {tools.map((t) => (
                      <label
                        key={t.name}
                        className="flex items-start gap-2 py-1 text-[12px] text-ink-muted"
                      >
                        <input
                          type="checkbox"
                          data-testid={`${testIdPrefix}-tool-${t.name}`}
                          disabled={isSafe}
                          checked={isSafe || selected.has(t.name)}
                          onChange={() => toggle(t.name)}
                          className="mt-[3px] h-[13px] w-[13px] shrink-0 accent-accent"
                        />
                        <span className="min-w-0">
                          <span className="font-mono text-[12px] text-ink">{t.name}</span>
                          {t.description ? (
                            <span className="ml-1.5 text-ink-muted">— {truncate(t.description, 90)}</span>
                          ) : null}
                        </span>
                      </label>
                    ))}
                  </div>
                </div>
              );
            })}
        </div>
      )}
    </div>
  );
}

function truncate(s: string, n: number): string {
  return s.length <= n ? s : s.slice(0, n).trimEnd() + '…';
}
