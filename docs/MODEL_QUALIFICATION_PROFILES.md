# Model qualification profiles

Model qualification begins before a provider loads a model. A profile freezes
the sourced facts and selected qualification configuration for one exact model
artifact while leaving provider lifecycle and benchmark policy separate.

This is reusable intake infrastructure, not a model-specific optimization
layer. Runtime code must never branch on a model family or profile identifier.

## Contract

The v1 schema is
[`dev/model-qualification-profile.schema.json`](../dev/model-qualification-profile.schema.json).
A profile records:

- exact model identity and optional artifact metadata;
- HTTPS sources with immutable revisions and retrieval timestamps;
- source-backed runtime compatibility claims;
- published recommendations kept separate from the selected qualification arm;
- the context, output, and generation configuration selected for qualification;
- every researched generation control, including controls the current runtime
  cannot apply.

`dev/ModelQualificationProfile.psm1` validates and compiles the document before
provider startup. The compiler applies only settings represented by the shared
Sir Thaddeus runtime contract: context window, maximum output, and temperature.
Every other generation field is retained under `unsupported_settings` in the
provider plan and scorecard. Unsupported values are never silently discarded
or approximated. If selected context or temperature differs from the sourced
recommendation, `qualification.override_reason` is mandatory and the compiled
artifact records the exact difference.

## Separation of concerns

```text
official sources + pinned revisions
                 |
                 v
       model qualification profile
                 |
        deterministic compile
          /              \
 applied runtime fields   unsupported researched fields
          |                         |
          v                         v
 provider plan artifact       visible stop signal
          |
          v
 capability probes -> capability-keyed certificates -> benchmark eligibility
```

Published recommendations are an intake starting point. They are not
automatically the sampling controls for a cross-model benchmark. A benchmark
campaign must still freeze a comparable configuration and state any override.

Provider selection remains outside the model profile. The caller chooses
`lmstudio`, `llamacpp`, or `external`; the profile must contain sourced support
for that backend. Executable paths, model paths, ports, and API endpoints remain
machine or provider configuration rather than model-family knowledge.

## Plan-only use

Compile the profile and provider plan without starting a provider or making a
model call:

```powershell
./dev/model-intake.ps1 `
  -ProfilePath C:\research\profiles\candidate.json `
  -Backend llamacpp `
  -LlamaServerPath C:\tools\llama.cpp\llama-server.exe `
  -ModelPath D:\models\candidate.gguf `
  -SettingsTemplate ./SirThaddeus.Settings.template.json `
  -PlanOnly
```

The resulting `provider-plan.json` contains the profile ID, SHA-256, sources,
applied settings, and unsupported settings. Loading is permitted only after
that artifact is reviewable.

## Capability matrix

Production certificates are keyed by capability, exact configuration
fingerprint, tool-contract version, and probe version. The existing Wiki-write
fields and endpoints remain compatibility aliases, while generic preferences,
certificates, and capability-keyed API routes provide the reusable spine.

Unknown capabilities fail closed. A new capability must register a bounded,
side-effect-free probe and deterministic classifier; adding a model never
requires production branching.

The implementation audit and remaining boundaries are recorded in
[`research/MODEL_AGNOSTIC_ONBOARDING_AUDIT_2026-08-02.md`](research/MODEL_AGNOSTIC_ONBOARDING_AUDIT_2026-08-02.md).
