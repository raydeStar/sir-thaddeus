# Model tier calibration

Sir Thaddeus uses a small, fixed model panel to discover improvements and test
whether they transfer. The panel is an evaluation policy, not a production
router. Production capabilities remain governed by exact-configuration
certificates and user controls; runtime code must never infer support from a
model name, family, or parameter count.

## Current panel

| Evaluation role | Current exact-model family | Purpose | What a failure means |
| --- | --- | --- | --- |
| Floor and stress model | LFM 2.5 1.2B | Preserve safety, conversational quality, and fail-closed behavior at the smallest supported tier | Withhold the failed optional capability in Auto mode; do not veto a gain that transfers safely to the primary anchor |
| Edge-default candidate | LFM 2.5 2.6B | Measure the likely everyday local deployment point after provider and capability qualification | Keep it unqualified or capability-limited until its exact configuration passes |
| Primary discovery anchor | LFM 2.5 8B-A1B | Select and promote generalized harness mechanisms under a practical local resource budget | Reject the candidate if it misses its predeclared fixed-model gate |
| Ceiling and transfer model | Gemma 4 26B-A4B | Check whether a retained mechanism remains useful and safe when the model has more capacity | Treat a loss as a transfer warning; never count the stronger model as local-model uplift |

These roles can change when fresh evidence changes. Artifact, quantization,
provider, prompt, context, sampling, tool-contract, and runtime hashes still
belong in each experiment manifest.

## Promotion sequence

1. Predeclare one mechanism, the exact primary-anchor configuration, controls,
   outcome metric, guardrails, resource ceiling, call budget, and rollback.
2. Compare raw, production-prompt/no-tools, equal-tools direct, unchanged Sir,
   and candidate arms as applicable on fresh development tasks.
3. Reject a losing candidate immediately. A credible win must repeat exactly
   before any disjoint validation set is consumed.
4. After validation, run focused transfer checks on one lower configuration and
   the ceiling configuration. Do not average their scores together or use one
   tier to conceal a regression in another.
5. A lower tier may safely receive less functionality. If the capability is
   optional, the exact configuration fails closed in Auto mode while On and Off
   remain explicit user choices. Safety, false-success, and conversational
   guardrails are not optional.

This sequence changes which model is used to find broadly useful mechanisms;
it does not replace the fixed-model controls required to claim harness uplift.

## Capability delivery

Capability delivery is configuration-specific:

```text
evaluation panel selects promising mechanisms
                      |
                      v
       exact configuration runs bounded probes
                      |
           +----------+-----------+
           |                      |
       certified             limited or failed
           |                      |
 Auto exposes capability     Auto withholds capability
           |                      |
           +------ On / Off remain user controlled ------+
```

The certificate fingerprint uses only observable configuration and contract
fields. The production path does not contain thresholds such as `8B+` and does
not branch on LFM, Gemma, or another family.

## Why the anchor changed

The sealed 100-case panel showed repeated strict-family uplift for LFM 8B-A1B
and Gemma 26B while LFM 1.2B received little strict-family orchestration lift.
The later reviewed 64-case scorecard also showed that larger is not a capability
guarantee: 8B improved overall breadth but regressed verified Wiki mutations.
Together, those results support using 8B to discover mechanisms while retaining
exact-configuration certification for delivery.

LFM 1.2B remains valuable. It is a fast floor, a safety stress test, and a real
deployment option for certified operations. Its inability to pass a particular
capability probe means that capability does not trickle down automatically; it
does not mean the model or product tier is retired.

## Recalibration triggers

Revisit the panel only when at least one of these changes materially:

- a new exact model offers a better quality, latency, memory, or energy point;
- the primary anchor repeatedly shows no causal headroom on representative
  outcome tasks;
- failures cease to transfer between the anchor and ordinary deployments;
- provider or tool-call compatibility changes; or
- at least 300 labeled outcomes justify an explicit escalation study.

Record a new qualification profile and fresh evidence before changing roles.
Do not infer a role from vendor claims or parameter count alone.
