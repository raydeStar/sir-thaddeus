import { useCallback, useEffect, useState } from 'react';
import { AlertCircle, ChevronDown, ChevronRight, RefreshCw, X } from 'lucide-react';
import type {
  PermissionDeveloperOverride,
  PermissionPolicy,
  PermissionsSettings,
  SettingsDocument,
} from '@thaddeus/shared-types';
import {
  getPermissionCatalog,
  type PermissionCatalog,
  type PermissionCatalogTool,
} from '../../lib/permissionsApi';

/**
 * Cascading per-tool permission editor rendered inside Settings →
 * Permissions (below the privacy/approval toggles): developer override →
 * group policy → per-tool override. The settings document
 * (`doc.permissions`) is the source of truth for every value; the catalog
 * fetched from the runtime only supplies the tool inventory per group.
 * Saving rides the settings page's existing PUT flow.
 */

type GroupKey = 'screen' | 'files' | 'system' | 'web' | 'memoryRead' | 'memoryWrite';

const GROUP_ORDER: ReadonlyArray<{ key: GroupKey; label: string; description: string }> = [
  { key: 'screen', label: 'Screen', description: 'Screenshots and inspecting the active window.' },
  { key: 'files', label: 'Files', description: 'Reading local files and directory listings.' },
  { key: 'system', label: 'System', description: 'Shell commands, clipboard, and other machine access.' },
  { key: 'web', label: 'Web', description: 'Search, weather, places, feeds — anything that reaches the network.' },
  { key: 'memoryRead', label: 'Memory (read)', description: 'Looking up stored facts and preferences.' },
  { key: 'memoryWrite', label: 'Memory (write)', description: 'Saving, updating, or removing stored facts.' },
];

/** Groups the developer override applies to (memory is exempt). */
const DANGEROUS_GROUPS: ReadonlySet<GroupKey> = new Set(['screen', 'files', 'system', 'web']);

const DEFAULT_PERMISSIONS: PermissionsSettings = {
  developerOverride: 'none',
  screen: 'ask',
  files: 'ask',
  system: 'ask',
  web: 'ask',
  memoryRead: 'always',
  memoryWrite: 'ask',
};

const POLICY_LABELS: Record<PermissionPolicy, string> = {
  off: 'Off',
  ask: 'Ask',
  always: 'Always',
};

const POLICY_OPTIONS: ReadonlyArray<{ value: PermissionPolicy; label: string }> = [
  { value: 'off', label: 'Off' },
  { value: 'ask', label: 'Ask' },
  { value: 'always', label: 'Always' },
];

const DEV_OVERRIDE_OPTIONS: ReadonlyArray<{ value: PermissionDeveloperOverride; label: string }> = [
  { value: 'none', label: 'None (use group policies)' },
  { value: 'off', label: 'Off — block everything' },
  { value: 'ask', label: 'Ask — prompt for everything' },
  { value: 'always', label: 'Always — allow everything' },
];

const selectCls =
  'block w-full appearance-none rounded-xl border border-line bg-canvas-raised px-3.5 py-2 pr-9 text-sm text-ink transition-colors focus:border-accent-ring focus:outline-none focus:ring-2 focus:ring-accent/20';

export function ToolPolicyEditor({
  doc,
  setDoc,
}: {
  doc: SettingsDocument;
  setDoc: (d: SettingsDocument) => void;
}) {
  const [catalog, setCatalog] = useState<PermissionCatalog | null>(null);
  const [catalogError, setCatalogError] = useState<string | null>(null);
  const [catalogLoading, setCatalogLoading] = useState(true);
  const [expanded, setExpanded] = useState<ReadonlySet<GroupKey>>(new Set());

  const loadCatalog = useCallback(() => {
    setCatalogLoading(true);
    setCatalogError(null);
    getPermissionCatalog()
      .then((c) => setCatalog(c))
      .catch((e: Error) => setCatalogError(e.message))
      .finally(() => setCatalogLoading(false));
  }, []);

  useEffect(() => {
    loadCatalog();
  }, [loadCatalog]);

  // Display values come from the doc when present, defaults otherwise.
  // Edits seed the whole permissions slice from defaults on first touch so a
  // null slice never round-trips as a partial object.
  const perms = doc.permissions ?? DEFAULT_PERMISSIONS;
  const overrides = perms.toolOverrides ?? {};

  const updatePermissions = (patch: Partial<PermissionsSettings>) => {
    setDoc({ ...doc, permissions: { ...(doc.permissions ?? DEFAULT_PERMISSIONS), ...patch } });
  };

  /** Sets/clears one tool override; drops `toolOverrides` entirely when empty. */
  const setToolOverride = (toolName: string, policy: PermissionPolicy | null) => {
    const next: Record<string, PermissionPolicy> = { ...overrides };
    if (policy === null) {
      delete next[toolName];
    } else {
      next[toolName] = policy;
    }
    updatePermissions({ toolOverrides: Object.keys(next).length > 0 ? next : undefined });
  };

  const resetGroupOverrides = (toolNames: ReadonlyArray<string>) => {
    const drop = new Set(toolNames);
    const next: Record<string, PermissionPolicy> = {};
    for (const [name, policy] of Object.entries(overrides)) {
      if (!drop.has(name)) next[name] = policy;
    }
    updatePermissions({ toolOverrides: Object.keys(next).length > 0 ? next : undefined });
  };

  /** Static resolution mirror: toolOverride ?? dev-override-if-dangerous ?? groupPolicy. */
  const effectiveFor = (groupKey: GroupKey, toolName: string): PermissionPolicy => {
    const override = overrides[toolName];
    if (override) return override;
    if (perms.developerOverride !== 'none' && DANGEROUS_GROUPS.has(groupKey)) {
      return perms.developerOverride;
    }
    return perms[groupKey];
  };

  const toggleExpanded = (key: GroupKey) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  // Tool names the catalog knows about, for spotting "other" overrides that
  // belong to tools this build doesn't list (preserved, never silently lost).
  const knownToolNames = new Set(
    (catalog?.groups ?? []).flatMap((g) => g.tools.map((t) => t.name)),
  );
  const unknownOverrides = Object.entries(overrides).filter(
    ([name]) => !knownToolNames.has(name),
  );

  return (
    <div className="space-y-6" data-testid="settings-permissions-panel">
      <section className="space-y-5 border-b border-line pb-10">
        <header>
          <h2 className="text-[15px] font-semibold tracking-tight text-ink">Developer override</h2>
          <p className="mt-1 text-[13px] text-ink-muted">
            When set, overrides the Screen, Files, System, and Web group policies (memory is
            unaffected). Leave on None outside of development.
          </p>
        </header>
        <div className="max-w-xs">
          <PolicySelect
            testId="settings-permissions-developer-override"
            value={perms.developerOverride}
            options={DEV_OVERRIDE_OPTIONS}
            onChange={(v) => updatePermissions({ developerOverride: v as PermissionDeveloperOverride })}
          />
        </div>
      </section>

      <section className="space-y-4">
        <header>
          <h2 className="text-[15px] font-semibold tracking-tight text-ink">Capability groups</h2>
          <p className="mt-1 text-[13px] text-ink-muted">
            Each group has a policy: Off blocks its tools, Ask prompts every time, Always allows
            silently. Expand a group to override individual tools.
          </p>
        </header>

        {catalogError ? (
          <div
            data-testid="settings-permissions-catalog-error"
            className="flex flex-wrap items-center gap-3 rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-3"
          >
            <AlertCircle className="h-4 w-4 shrink-0 text-amber-600" strokeWidth={1.75} />
            <p className="min-w-0 flex-1 text-[13px] text-ink">
              Couldn't load the tool list from the runtime, so per-tool overrides are hidden.
              Group policies still work.
            </p>
            <button
              type="button"
              data-testid="settings-permissions-catalog-retry"
              onClick={loadCatalog}
              className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3 py-1.5 text-xs font-medium text-ink transition hover:bg-accent-soft"
            >
              <RefreshCw className="h-3.5 w-3.5" strokeWidth={1.75} />
              Retry
            </button>
          </div>
        ) : null}

        {GROUP_ORDER.map(({ key, label, description }) => (
          <GroupCard
            key={key}
            groupKey={key}
            label={label}
            description={description}
            policy={perms[key]}
            developerOverride={perms.developerOverride}
            tools={catalog?.groups.find((g) => g.key === key)?.tools ?? null}
            catalogLoading={catalogLoading}
            overrides={overrides}
            expanded={expanded.has(key)}
            onToggleExpanded={() => toggleExpanded(key)}
            onPolicyChange={(v) => updatePermissions({ [key]: v } as Partial<PermissionsSettings>)}
            onToolOverride={setToolOverride}
            onResetOverrides={resetGroupOverrides}
            effectiveFor={effectiveFor}
          />
        ))}
      </section>

      {unknownOverrides.length > 0 ? (
        <section className="space-y-4 border-t border-line pt-8">
          <header>
            <h2 className="text-[15px] font-semibold tracking-tight text-ink">Other overrides</h2>
            <p className="mt-1 text-[13px] text-ink-muted">
              Overrides for tools this build doesn't list — possibly from a module or another
              version. They still apply; remove any you no longer want.
            </p>
          </header>
          <ul
            data-testid="settings-permissions-other-overrides"
            className="divide-y divide-line rounded-xl border border-line bg-canvas-raised"
          >
            {unknownOverrides.map(([name, policy]) => (
              <li key={name} className="flex items-center gap-3 px-4 py-2.5">
                <code className="min-w-0 flex-1 truncate font-mono text-[13px] text-ink">{name}</code>
                <span className="rounded-full bg-canvas-sunken px-2 py-0.5 text-[11px] font-medium text-ink-muted">
                  {POLICY_LABELS[policy]}
                </span>
                <button
                  type="button"
                  aria-label={`Remove override for ${name}`}
                  data-testid={`settings-permissions-remove-override-${name}`}
                  onClick={() => setToolOverride(name, null)}
                  className="rounded-full p-1 text-ink-subtle transition hover:bg-canvas-sunken hover:text-ink"
                >
                  <X className="h-4 w-4" strokeWidth={1.75} />
                </button>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}

function GroupCard({
  groupKey,
  label,
  description,
  policy,
  developerOverride,
  tools,
  catalogLoading,
  overrides,
  expanded,
  onToggleExpanded,
  onPolicyChange,
  onToolOverride,
  onResetOverrides,
  effectiveFor,
}: {
  groupKey: GroupKey;
  label: string;
  description: string;
  policy: PermissionPolicy;
  developerOverride: PermissionDeveloperOverride;
  tools: PermissionCatalogTool[] | null;
  catalogLoading: boolean;
  overrides: Record<string, PermissionPolicy>;
  expanded: boolean;
  onToggleExpanded: () => void;
  onPolicyChange: (v: PermissionPolicy) => void;
  onToolOverride: (toolName: string, policy: PermissionPolicy | null) => void;
  onResetOverrides: (toolNames: ReadonlyArray<string>) => void;
  effectiveFor: (groupKey: GroupKey, toolName: string) => PermissionPolicy;
}) {
  const toolNames = (tools ?? []).map((t) => t.name);
  const overrideCount = toolNames.filter((n) => overrides[n] !== undefined).length;
  const devOverridden = developerOverride !== 'none' && DANGEROUS_GROUPS.has(groupKey);

  return (
    <div
      data-testid={`settings-permissions-group-${groupKey}`}
      className="rounded-xl border border-line bg-canvas-raised"
    >
      <div className="flex flex-wrap items-center gap-3 px-4 py-3.5 sm:flex-nowrap">
        <button
          type="button"
          data-testid={`settings-permissions-expand-${groupKey}`}
          onClick={onToggleExpanded}
          aria-expanded={expanded}
          disabled={tools === null}
          className="flex min-w-0 flex-1 items-center gap-2.5 text-left disabled:cursor-default"
        >
          {expanded ? (
            <ChevronDown className="h-4 w-4 shrink-0 text-ink-subtle" strokeWidth={1.75} />
          ) : (
            <ChevronRight className="h-4 w-4 shrink-0 text-ink-subtle" strokeWidth={1.75} />
          )}
          <span className="min-w-0">
            <span className="flex flex-wrap items-center gap-2">
              <span className="text-[14px] font-medium text-ink">{label}</span>
              {devOverridden ? (
                <span className="rounded-full bg-amber-500/15 px-2 py-0.5 text-[10px] font-medium text-amber-600">
                  dev override: {POLICY_LABELS[developerOverride as PermissionPolicy]}
                </span>
              ) : null}
            </span>
            <span className="mt-0.5 block text-[12px] text-ink-muted">{description}</span>
            <span
              className="mt-0.5 block text-[11px] text-ink-subtle"
              data-testid={`settings-permissions-counts-${groupKey}`}
            >
              {tools === null
                ? catalogLoading
                  ? 'Loading tools…'
                  : 'Tool list unavailable'
                : `${tools.length} ${tools.length === 1 ? 'tool' : 'tools'} · ${overrideCount} ${overrideCount === 1 ? 'override' : 'overrides'}`}
            </span>
          </span>
        </button>
        <div className="w-32 shrink-0">
          <PolicySelect
            testId={`settings-permissions-policy-${groupKey}`}
            value={policy}
            options={POLICY_OPTIONS}
            onChange={(v) => onPolicyChange(v as PermissionPolicy)}
          />
        </div>
      </div>

      {expanded && tools !== null ? (
        <div className="border-t border-line px-4 py-3">
          {tools.length === 0 ? (
            <p className="py-1 text-[13px] text-ink-muted">No tools in this group.</p>
          ) : (
            <ul className="divide-y divide-line/60">
              {tools.map((tool) => (
                <ToolRow
                  key={tool.name}
                  groupKey={groupKey}
                  tool={tool}
                  override={overrides[tool.name]}
                  effective={effectiveFor(groupKey, tool.name)}
                  onChange={(policy) => onToolOverride(tool.name, policy)}
                />
              ))}
            </ul>
          )}
          {overrideCount > 0 ? (
            <div className="mt-2 flex justify-end">
              <button
                type="button"
                data-testid={`settings-permissions-reset-${groupKey}`}
                onClick={() => onResetOverrides(toolNames)}
                className="rounded-full px-3 py-1.5 text-xs font-medium text-ink-muted transition hover:bg-canvas-sunken hover:text-ink"
              >
                Reset group overrides
              </button>
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function ToolRow({
  groupKey,
  tool,
  override,
  effective,
  onChange,
}: {
  groupKey: GroupKey;
  tool: PermissionCatalogTool;
  override: PermissionPolicy | undefined;
  effective: PermissionPolicy;
  onChange: (policy: PermissionPolicy | null) => void;
}) {
  const hasOverride = override !== undefined;
  return (
    <li
      className="flex items-center gap-3 py-2"
      data-testid={`settings-permissions-tool-${groupKey}-${tool.name}`}
    >
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <code className="truncate font-mono text-[13px] text-ink">{tool.name}</code>
          {hasOverride ? (
            <span
              data-testid={`settings-permissions-override-badge-${tool.name}`}
              className="rounded-full bg-accent-soft px-2 py-0.5 text-[10px] font-medium text-accent"
            >
              override
            </span>
          ) : null}
        </div>
        {!hasOverride ? (
          <p className="mt-0.5 text-[11px] text-ink-subtle">inherits {POLICY_LABELS[effective]}</p>
        ) : null}
      </div>
      <div className="w-32 shrink-0">
        <PolicySelect
          testId={`settings-permissions-tool-policy-${tool.name}`}
          value={hasOverride ? override : 'inherit'}
          options={[{ value: 'inherit', label: 'Inherit' }, ...POLICY_OPTIONS]}
          onChange={(v) => onChange(v === 'inherit' ? null : (v as PermissionPolicy))}
          highlighted={hasOverride}
        />
      </div>
    </li>
  );
}

function PolicySelect({
  testId,
  value,
  options,
  onChange,
  highlighted = false,
}: {
  testId: string;
  value: string;
  options: ReadonlyArray<{ value: string; label: string }>;
  onChange: (v: string) => void;
  highlighted?: boolean;
}) {
  return (
    <div className="relative">
      <select
        data-testid={testId}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className={`${selectCls} ${highlighted ? 'border-accent-ring text-accent' : ''}`}
      >
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
      <ChevronDown
        className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-ink-subtle"
        strokeWidth={1.75}
      />
    </div>
  );
}

