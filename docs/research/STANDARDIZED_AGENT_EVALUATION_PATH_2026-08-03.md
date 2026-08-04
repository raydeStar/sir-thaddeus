# Standardized Agent Evaluation Path

Date: 2026-08-03

## Verdict

**Stop making the solved typed-update slice harder merely to create score
headroom. Move the next harness campaign onto externally maintained,
execution-verified tasks.** The fresh semantic-delta prerequisite showed that
unchanged LFM 2.5 1.2B already completes `6/6` explicit typed Wiki updates.
Harder wording would create a lower baseline, not an uplift. Uplift exists only
when one generalized mechanism beats the unchanged harness on the same fresh
tasks and reproduces on disjoint validation.

The product is not generally solved. The reviewed 64-case scorecard remains
`23/64` strict on LFM 2.5 1.2B, and the semantic-delta controls exposed forbidden
writes on missing-value and ambiguous-target requests. The next useful
difficulty should come from state depth, uncertainty, policy, and external tool
protocols rather than obscure phrasing around an operation that already passes.

## Difficulty without benchmark theatre

Retain the reviewed 64-case bank as an immutable continuity bank. If a local
extension is needed, cap the next additive version at 32 cases so the reviewed
portfolio remains below 100 tasks:

| Family | Maximum | What makes it harder | Required proof |
| --- | ---: | --- | --- |
| Missing or ambiguous information | 8 | A required value or unique target is absent | No mutation; useful clarification |
| Stateful composition | 8 | Two or three dependent reads and writes | Exact final state and correct intermediate constraints |
| Capability interference | 8 | Renamed schemas, irrelevant tools, or plausible distractors | Correct capability and arguments without benchmark-name routing |
| Failure and recovery | 8 | Tool denial, stale state, partial failure, interruption, or rollback | Safe stop or exact recovery with an auditable receipt |

These are difficulty dimensions, not four mechanisms. Authoring a harder bank
does not authorize a production change. Run unchanged first, cluster failures
by causal seam, and require an oracle to establish headroom before implementing
anything. Use semantic mutations and new entities; do not tune against the
reviewed 64 cases or copy an external benchmark's hidden answers.

## Standardized target order

| Priority | Target | Role for Sir Thaddeus | Why it fits | Important limitation |
| ---: | --- | --- | --- | --- |
| 1 | [MCPMark Verified](https://github.com/eval-sys/mcpmark) | Fixed-model harness-to-harness study | Real MCP services, isolated environments, automated verifiers, resource metrics, an easy tier for lightweight models, and a 127-task standard tier | Sir must be integrated as a disclosed custom scaffold; published model scores are not automatically harness comparisons |
| 2 | [ToolSandbox](https://github.com/apple/ToolSandbox) | Safety and causal diagnostic | Stateful tools, implicit dependencies, user simulation, intermediate/final milestones, and explicit insufficient-information scenarios match the newly observed safety seam | Use as transfer evidence and a focused validation source, not as a claim that Sir won a current competition |
| 3 | [tau-bench](https://github.com/sierra-research/tau2-bench) | First public custom-harness submission | Multi-turn user/tool interaction, policy following, stateful domains, objective actions, a maintained leaderboard, and an explicit custom-submission category for modified orchestration | Full submissions are expensive: complete domain tasks and four or more trials are preferred |
| 4 | [AgentBeats](https://docs.agentbeats.org/) | Interoperability and future competition readiness | Standard A2A/MCP assessment interface and controller lifecycle align with a thin external adapter | The 2026 AgentX Phase 2 competition ran March 2 through June 2; treat the platform as active infrastructure, not an open prize round |
| 5 | [BFCL V4](https://gorilla.cs.berkeley.edu/leaderboard) | Model/tool-call intake control | Reproducible function-call, multi-turn, hallucination, latency, and cost measurements | Primarily measures model function calling; it cannot by itself prove Sir's harness improved |
| 6 | [GAIA](https://huggingface.co/gaia-benchmark) | Late general-assistant confirmation | Public assistant tasks combine reasoning, browsing, files, multimodality, and tool use | Broad capability and model knowledge are heavily confounded; run only after web/file adapters are stable |

MCPMark is the best first engineering target because it can compare the same
frozen local model under its built-in scaffold and a Sir-backed scaffold while
keeping tasks and verifiers fixed. tau-bench is the better eventual public
leaderboard story because it explicitly labels custom orchestration instead of
presenting it as an off-the-shelf model run.

## Adapter boundary

Do not extract the production orchestration core into a competition repository.
Keep a thin, disposable adapter outside production behavior:

```text
external task protocol
    -> benchmark-owned episode adapter
    -> Sir headless/runtime conversation contract
    -> shared production pipeline
    -> audited benchmark-provided MCP tools
    -> benchmark-owned state verifier
```

The adapter may translate lifecycle, message, and tool-catalog shapes. It must
not select behavior by benchmark name, inject expected answers, weaken
permissions, own the model prompt, or duplicate the Sir reasoning loop. Any
generic benchmark tool catalog must still enter through the audited MCP
boundary. Third-party benchmark dependencies, credentials, containers, task
data, and scorer code stay in the evaluator or a dedicated adapter repository,
not in production assemblies.

## First campaign: MCPMark filesystem easy

Start with a zero-external-service-credential, ten-task feasibility campaign
from the current pinned MCPMark repository. This is small enough to reject an
incompatible adapter without spending a Verified standard or leaderboard run.

### Phase 0: no model calls

1. Pin the current MCPMark commit and record the easy-suite task and verifier
   hashes; record the distinct Verified standard suite identity separately.
2. Run the upstream environment inside Linux Docker or WSL and prove isolated
   filesystem setup, teardown, and verifier execution; do not assume an
   unsupported native-Windows path.
3. Map one benchmark episode through the existing authenticated runtime API.
4. Prove the benchmark tool catalog reaches Sir through the normal audited MCP
   boundary and disappears after teardown.
5. Record calls, tokens, latency, peak memory/VRAM, tool results, and final
   verifier state without copying expected outcomes into model messages.

### Phase 1: ten-case development

Freeze one exact local model configuration. Compare:

1. MCPMark's unchanged built-in scaffold;
2. raw or direct equal-tools control where the upstream protocol supports it;
3. unchanged Sir through the thin adapter.

Run the unchanged arms before proposing a Sir behavior change. If Sir is at the
ceiling, advance to harder official MCPMark tasks. If every arm is at the floor,
use oracle-tool or gold-state diagnostics before deciding whether the model or
adapter is the limit. Only a repeated, causally aligned miss cluster may open a
product experiment.

### Promotion gate for the adapter

The adapter is infrastructure, not capability. Promote it when all ten episode
lifecycles are reproducible, setup and cleanup are exact, task and verifier
hashes are frozen, no production code contains benchmark identity, and every
result is attributable to the intended product path. A later behavior candidate
needs its own numeric win gate, exact repeat, disjoint validation, safety gates,
and rollback.

## Public campaign sequence

1. **MCPMark filesystem easy, one frozen model:** adapter feasibility from the
   current pinned repository and the first harness-to-harness comparison.
2. **MCPMark Verified standard subset:** only after the easy adapter gate;
   retain official tasks unchanged and predeclare the case budget.
3. **Fixed-model repeat and transfer:** establish a Sir gain on one frozen local
   model first, then test one 8B-or-larger configuration as transfer evidence.
4. **ToolSandbox insufficient-information slice:** investigate clarification and
   ambiguity only if fresh official outcomes reproduce the safety cluster.
5. **tau-bench custom submission:** run complete tasks and the required repeated
   trials only after explicit large-campaign acknowledgement.
6. **AgentBeats A2A publication:** wrap the same adapter core with the
   AgentBeats controller when a new competition or useful assessment target is
   available.
7. **BFCL and GAIA:** publish them in separate capacity/general-assistant
   columns; never blend them into the harness-uplift headline.

## World-class evidence standard

A credible public result should include the pinned upstream commit, task and
verifier hashes, exact model artifact and provider configuration, unchanged and
candidate trajectories, paired wins and losses, exact repeats, confidence
intervals where sample size permits them, tool/model calls, tokens, latency,
VRAM, cost per verified outcome, failure taxonomy, and a public adapter whose
behavior does not depend on task identity. Stronger-model escalation remains a
separate metric.

This path raises the difficulty and the credibility of the measurement. It
does not guarantee uplift, and that is the point: a standardized harness win is
valuable only when Sir earns it against an unchanged scaffold on somebody
else's tasks and verifiers.

## Primary sources

- [MCPMark Verified repository and task protocol](https://github.com/eval-sys/mcpmark)
- [ToolSandbox project](https://machinelearning.apple.com/research/toolsandbox-stateful-conversational-llm-benchmark)
- [tau-bench repository](https://github.com/sierra-research/tau2-bench)
- [tau-bench leaderboard submission guide](https://github.com/sierra-research/tau2-bench/blob/main/docs/leaderboard-submission.md)
- [AgentBeats documentation](https://docs.agentbeats.org/)
- [AgentX-AgentBeats competition schedule](https://rdi.berkeley.edu/agentx-agentbeats.html)
- [Berkeley Function Calling Leaderboard V4](https://gorilla.cs.berkeley.edu/leaderboard)
- [GAIA benchmark](https://huggingface.co/gaia-benchmark)
