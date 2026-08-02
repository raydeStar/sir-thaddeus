# Model-agnostic onboarding audit

Date: 2026-08-02  
Product baseline: `2db0a525c8a0d294332c73caabc34e1cf32c2456`  
Primary scorecard: product quality and evaluation infrastructure

## Verdict

The repository already had the right safety idea—exact-configuration capability
certificates—but two implementation seams were too narrow:

1. generic certificate records were stored and queried through Wiki-specific
   settings and routes; and
2. research intake accepted provider arguments directly, with no sourced model
   profile or deterministic report of settings the runtime could not apply.

The candidate generalizes those seams without loading a model, running a
benchmark, or changing current Wiki-write policy.

## Retained architecture

- Production and research still share the OpenAI-compatible client boundary.
- Provider lifecycle remains in evaluation tooling, outside desktop/headless
  product hosts.
- Capability certificates remain advisory tool-menu narrowing; permissions,
  target guards, auditing, and postconditions remain authoritative.
- Existing Wiki `Auto / On / Off` settings and endpoints remain compatibility
  aliases and keep precedence over the new generic mirror.
- Published recommendations and frozen cross-model benchmark controls remain
  distinct. A deviation requires an explicit profile override reason.

## Corrections

### Pre-load model profiles

The v1 JSON schema records exact model identity, optional artifact metadata,
pinned HTTPS sources, documented runtime support, published recommendations,
and the selected qualification configuration. Compilation occurs before the
provider plan can start a process.

The compiler currently applies only shared runtime controls: context window,
maximum output, and temperature. It retains every additional generation field
as unsupported evidence. This is deliberately fail-visible; no adapter guesses
how one provider spells or approximates a missing control.

### Capability matrix

Settings now support capability-keyed preferences and certificates. Policy can
evaluate any capability against its own probe version, tool-contract version,
and exact configuration fingerprint. A cache-only collection endpoint returns
the registered capability matrix. Unknown API capability keys fail closed.

The Wiki-specific surface is preserved so existing UI and saved settings do
not change behavior. Retesting mirrors Wiki data into the generic store while
the legacy mode remains authoritative until a future explicit UI migration.

### Model-specific default removal

Fresh settings previously selected `liquid/lfm2.5-1.2b` as the gatekeeper model
in two settings systems, the template, and the UI placeholder. Those defaults
are now empty. Existing saved choices remain untouched; new installations must
select a verification model explicitly or let research intake reuse the exact
primary model.

## Extension rules

Adding a model requires data, not runtime code:

1. research and pin authoritative sources;
2. create a profile and compile it with `-PlanOnly`;
3. review applied, unsupported, and overridden settings;
4. start the selected provider only after the plan is accepted;
5. run registered capability probes and read the matrix; and
6. run only benchmark routes whose prerequisites are satisfied.

Adding a capability is a separate product change. It requires a registered,
bounded, side-effect-free probe, deterministic classification, its own versions,
and explicit runtime enforcement. It must not add model-family branches.

## Remaining boundaries

- Wiki write is still the only registered production capability probe. The
  storage, policy, and API are generic; probe implementations remain explicit.
- The compiler validates provenance claims and pinned revisions but does not
  browse or decide which sources are authoritative. Research produces the
  profile; intake verifies and preserves it.
- Only context, output limit, and temperature exist in shared runtime settings.
  Other controls remain visible as unsupported until a provider-neutral product
  contract is separately justified.
- This work proves structure and compatibility, not model quality or benchmark
  uplift. No model/provider request was made.

## Verification

- Model provider adapter: 65 assertions.
- Model qualification profile: 22 assertions across two unrelated synthetic
  models, two providers, mismatch/override failures, and plan-only compilation.
- Focused runtime settings, policy, matrix, and API tests: 25 passed, one live
  model test skipped by design.
- Focused runtime-host provider tests: 5 passed.
- Shared TypeScript and web typechecks: passed.
- Full `dev/test.ps1 -SkipScreenObserveHarness`: 2,920 passed, one live model
  test skipped; zero failures.
- Model calls: 0. Benchmark cases: 0.
