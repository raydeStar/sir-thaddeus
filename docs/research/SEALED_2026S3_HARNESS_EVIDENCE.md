# Local LLM Agent Harness Benchmark: Complete 2026 S3 Report

This is the canonical public report for the `sealed-2026s3` Sir Thaddeus
benchmark campaign. It consolidates the methodology, controls, accepted LFM,
Gemma, and Luna results, repeat stability, resource use, interruptions,
limitations, and reproduction pointers in one document.

The question was deliberately narrow:

> For a fixed model and sampling configuration, does the unchanged Sir
> Thaddeus agent harness complete more independently verified tasks than the
> same production prompt without tools?

## Verdict

**Supported for LFM 2.5 8B-A1B, Gemma 4 26B-A4B, and hosted Luna at low
reasoning effort. Not supported on the strict primary metric for LFM 2.5
1.2B, despite a large case-level improvement.**

- **LFM 1.2B:** full harness improved cases from 12/100 to 32/100, but strict
  family completion remained 1/25. This is useful partial-task lift, not a
  confirmed primary-metric win.
- **LFM 8B-A1B:** full harness improved strict families from 3/25 to 9/25 in
  both repeats (`p=0.03125`), with 48/100 and 49/100 cases completed.
- **Gemma 26B-A4B:** full harness improved strict families from 0/25 to 10/25
  in both repeats (`p=0.001953125`), with 56/100 cases completed both times.
- **Luna, low reasoning:** full harness improved strict families from 5/25 to
  12/25 and 9/25. The direction repeated, but only the first repeat crossed
  the conventional 0.05 significance threshold.

This is **hash-bound, repeat-tested engineering evidence** that tools and
orchestration improve verified outcomes for fixed models. It is not evidence
that the harness changed model weights, raised closed-book intelligence, or
established a universal model leaderboard.

The deterministic score totals and paired family analysis are suitable for
engineering decisions. The campaign is not yet labeled publication-ready by
the formal analyzer because several evidence-contract fields and causal
failure autopsies remain incomplete.

## Headline results

Every arm was run twice. A single value means both repeats matched; otherwise
repeat 1 and repeat 2 are separated by `/`.

| Fixed model | Raw cases | Production prompt, no tools | Same prompt, direct tools | Full harness | Strict families: no tools -> full | Verdict |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| LFM 2.5 1.2B Q4_K_M | 15 | 12 | 30 | **32** | 1/25 -> **1/25** | Case lift only |
| LFM 2.5 8B-A1B Q4_K_M | 24 | 22 | 42 | **48 / 49** | 3/25 -> **9/25** | Supported |
| Gemma 4 26B-A4B Q4_K_M | 23 | 5 / 8 | 43 / 46 | **56** | 0/25 -> **10/25** | Strongly supported |
| `gpt-5.6-luna`, low reasoning | 22 / 21 | 24 / 23 | 55 / 58 | **66 / 58** | 5/25 -> **12/25 / 9/25** | Supported, variable |

The table contains two different levels of evidence:

- **Cases completed** is the intuitive product metric. It answers how often a
  user received the required outcome.
- **Strict families completed** is the primary statistical metric. A family
  passes only when all four semantically related mutations pass, preventing
  four related cases from being treated as four independent samples.

## Methodology

### Frozen validation bank

- Bank ID: `sealed-2026s3`
- SHA-256:
  `89c96cda7e484453fc60a103d8b3d183dbaa9380c1fdf40ffc4b1c4b3718ecbd`
- 100 private, human-reviewed cases
- 25 independent semantic families
- 4 mutations per family
- Fabricated names, organizations, values, files, Wiki state, and evidence
- Expected outcomes hidden from every model
- Bank changes forbidden after the campaign was predeclared

Fabricated evidence is important. Public web questions can overlap with model
training data, making it unclear whether retrieval actually worked. These
tasks use invented facts and state, so a correct retrieval or extraction
answer must come through the evaluated capability path rather than memorized
public content.

The human review corrected ambiguous literal-answer contracts, missing
attachment tests, calculator wording, and state/no-action safety cases before
the confirmation runs. The bank was then frozen and reused without tuning.

### Four controlled arms

| Public name | Evaluator mode | What the model received |
| --- | --- | --- |
| Raw | `raw` | Minimal answer-only system prompt; no tools |
| Production prompt, no tools | `same_prompt_direct` | Real production prompt and response contract; no tools |
| Same prompt, direct tools | `same_prompt_tools_direct` | Same production prompt with declared tools, without full orchestration |
| Full harness | `harness_full` | Production tools, permissions, routing, retries, state handling, and verification |

The primary comparison was declared before execution:

`harness_full` versus `production_prompt_no_tools`

This control matters because raw-versus-harness alone mixes two changes:
production prompt scaffolding and tool/orchestration access. The direct-tools
arm further estimates how much value comes from tool availability versus the
additional production orchestration.

### Fixed execution configuration

- Product execution commit:
  `71e59d8a01f84970e71e0126867f522f0c4f630c`
- Temperature: `0`
- Context window: `8,192`
- Maximum output: `512` tokens
- Concurrency: `1`
- Two complete repeats per model and arm
- One local model loaded at a time
- Non-bank sentinels required before sealed evaluation
- Repository identity, model identity, quantization, and bank hash recorded

Local models were served through LM Studio on Windows 11 with an Intel
Core i9-14900K, 128 GB RAM, and an NVIDIA RTX 4090 with 24 GB VRAM. Luna used
`gpt-5.6-luna` through ephemeral Codex CLI requests with reasoning effort fixed
to `low`; conversation state was not reused between cases.

### Scoring

Primary scoring used deterministic contracts or independently observed final
state. It did not use an LLM judge.

Scored outcomes included:

- exact and literal response contracts;
- numeric values with predeclared tolerance;
- deterministic lexical clarification requirements;
- local-file extraction;
- tool-backed synthesis from fabricated fixtures;
- independently observed Wiki or file state after actions; and
- unchanged state plus truthful reporting for denied or ambiguous actions.

The primary metric was **strict family completion: 4 passes out of 4**.
Secondary measures included case completion, output validity, paired
wins/losses/ties, exact sign tests, bootstrap confidence intervals, repeat
agreement, model calls, latency, tokens, and peak VRAM.

## Primary family-level inference

| Model | Repeat | Family wins / losses / ties | Strict-family delta | Bootstrap 95% CI | Exact paired sign p |
| --- | ---: | ---: | ---: | ---: | ---: |
| LFM 1.2B | 1 | 1 / 1 / 23 | 0 points | -12 to +12 | 1.00000 |
| LFM 1.2B | 2 | 1 / 1 / 23 | 0 points | -12 to +12 | 1.00000 |
| LFM 8B-A1B | 1 | 6 / 0 / 19 | +24 points | +8 to +40 | 0.03125 |
| LFM 8B-A1B | 2 | 6 / 0 / 19 | +24 points | +8 to +40 | 0.03125 |
| Gemma 26B-A4B | 1 | 10 / 0 / 15 | +40 points | +20 to +60 | 0.001953 |
| Gemma 26B-A4B | 2 | 10 / 0 / 15 | +40 points | +20 to +60 | 0.001953 |
| Luna low | 1 | 7 / 0 / 18 | +28 points | +12 to +48 | 0.015625 |
| Luna low | 2 | 4 / 0 / 21 | +16 points | +4 to +32 | 0.12500 |

Gemma produced the strongest repeated statistical result. LFM 8B also crossed
the predeclared primary gate in both repeats. Luna was positive in both runs
but varied enough that only repeat 1 was conventionally significant. LFM 1.2B
did more individual work with the harness but did not generalize across all
four mutations of additional families.

## Full arm-by-arm results

### LFM 2.5 1.2B Q4_K_M

| Arm | Strict families | Passed cases | Valid outputs |
| --- | ---: | ---: | ---: |
| Raw | 2/25 both | 15/100 both | 95/100 both |
| Production prompt, no tools | 1/25 both | 12/100 both | 91/100 both |
| Same prompt, direct tools | 2/25 both | 30/100 both | 90/100 both |
| Full harness | 1/25 both | 32/100 both | 90/100 both |

Interpretation: direct capability access explains nearly all of the case-level
gain. The full harness added two cases over direct tools but no strict-family
gain over the no-tools control. This model appears to be at a capability floor
for consistently completing all four semantic mutations.

### LFM 2.5 8B-A1B Q4_K_M

| Arm | Strict families | Passed cases | Valid outputs |
| --- | ---: | ---: | ---: |
| Raw | 4/25 both | 24/100 both | 81/100 both |
| Production prompt, no tools | 3/25 both | 22/100 both | 62/100 both |
| Same prompt, direct tools | 5/25 / 6/24 eligible | 42/100 both | 88/100 both |
| Full harness | 9/25 both | 48/100 / 49/100 | 100/100 both |

Interpretation: the harness produced a repeated, statistically supported
strict-family lift. Direct tools did much of the work, while the full
orchestration path added cases, strict-family consistency, and output validity.
One direct-tools case in repeat 2 exceeded the maximum tool rounds, leaving 24
eligible families for that secondary arm. It does not affect the primary
full-versus-no-tools comparison.

### Gemma 4 26B-A4B Q4_K_M

| Arm | Strict families | Passed cases | Valid outputs |
| --- | ---: | ---: | ---: |
| Raw | 5/25 both | 23/100 both | 53/100 both |
| Production prompt, no tools | 0/25 both | 5/100 / 8/100 | 63/100 / 65/100 |
| Same prompt, direct tools | 6/25 / 7/25 | 43/100 / 46/100 | 83/100 / 86/100 |
| Full harness | 10/25 both | 56/100 both | 99/100 / 98/100 |

Interpretation: Gemma supplied the clearest evidence that production
orchestration adds value beyond merely exposing tools. Full harness exceeded
direct tools by 13 and 10 cases, and by 4 and 3 strict families. The formal
paired inference remains the predeclared comparison against no tools.

### Luna at low reasoning effort

| Arm | Strict families | Passed cases | Valid outputs |
| --- | ---: | ---: | ---: |
| Raw | 4/25 both | 22/100 / 21/100 | 84/100 / 86/100 |
| Production prompt, no tools | 5/25 both | 24/100 / 23/100 | 88/100 / 86/100 |
| Same prompt, direct tools | 9/25 both | 55/100 / 58/100 | 99/100 / 96/100 |
| Full harness | 12/25 / 9/25 | 66/100 / 58/100 | 99/100 / 98/100 |

Interpretation: the harness also helped a frontier-class hosted model. Tool
availability explains most of the gain. Full orchestration added another 11
cases and 3 strict families in repeat 1, then tied direct tools in repeat 2.
The correct conclusion is positive but variable—not deterministic replication.

## Repeat stability

| Model and arm | Pass/fail agreement | Byte-exact output agreement | Strict-family agreement |
| --- | ---: | ---: | ---: |
| LFM 1.2B raw | 100/100 | 100/100 | 25/25 |
| LFM 1.2B no tools | 100/100 | 100/100 | 25/25 |
| LFM 1.2B direct tools | 100/100 | 92/100 | 25/25 |
| LFM 1.2B full harness | 100/100 | 96/100 | 25/25 |
| LFM 8B raw | 100/100 | 100/100 | 25/25 |
| LFM 8B no tools | 100/100 | 100/100 | 25/25 |
| LFM 8B direct tools | 97/99 eligible | 76/99 eligible | 23/24 eligible |
| LFM 8B full harness | 95/100 | 73/100 | 25/25 |
| Gemma raw | 100/100 | 100/100 | 25/25 |
| Gemma no tools | 97/100 | 49/100 | 25/25 |
| Gemma direct tools | 93/100 | 53/100 | 24/25 |
| Gemma full harness | 94/100 | 69/100 | 25/25 |
| Luna raw | 99/100 | 38/100 | 25/25 |
| Luna no tools | 97/100 | 30/100 | 25/25 |
| Luna direct tools | 79/100 | 39/100 | 21/25 |
| Luna full harness | 82/100 | 36/100 | 22/25 |

Outcome agreement is the important signal; byte-identical prose is not
required for most tasks. Temperature zero did not make Luna deterministic,
because hosted infrastructure and model execution can still vary.

## Latency, calls, tokens, and VRAM

These are full-harness measurements for repeat 1 / repeat 2 on this execution
path, not universal speed or price claims.

| Model | Summed case latency | Model/provider calls | Estimated tokens | Peak VRAM | Approx. wall time for two four-arm grids |
| --- | ---: | ---: | ---: | ---: | ---: |
| LFM 1.2B | 520.6s / 506.9s | 261 / 261 | 218,404 / 218,400 | 3,111 / 3,077 MiB | 26 minutes |
| LFM 8B-A1B | 715.4s / 693.2s | 284 / 283 | 348,586 / 348,137 | 7,608 / 7,580 MiB | 47 minutes |
| Gemma 26B-A4B | 987.2s / 1,205.6s | 302 / 303 | 394,756 / 396,038 | 19,835 / 20,156 MiB | 74 minutes |
| Luna low | 3,063.1s / 3,285.5s | 379 / 373 | 448,093 / 441,050 | Hosted | 170 minutes |

Token accounting is adapter-specific and should not be used as a direct
provider-cost comparison. Local rows include a single loaded model at
concurrency one. Luna starts an ephemeral hosted request for each model call,
which preserves case isolation but contributes heavily to latency.

### Luna low versus historical high reasoning

The two Luna-low full-harness repeats averaged approximately 3,174 seconds of
summed case latency, versus approximately 3,855 seconds in the earlier
high-reasoning campaign: an estimated **17.6% reduction**.

Average full-harness completion was 62/100 at low effort versus 60/100
historically at high effort. Average strict-family completion was 10.5/25 in
both. This is a descriptive cross-wave comparison, not paired proof that low
reasoning is always faster or equally capable.

## Interruptions and accepted-run fidelity

### Gemma host restart

The host restarted after Gemma repeat 1 had completed all four 100-case
artifacts. The initial repeat-2 process printed its plan but created no scored
run directory. No partial results were salvaged.

After reboot:

1. the exact Gemma model and quantization were loaded alone;
2. all four non-bank sentinels passed; and
3. repeat 2 restarted from case 1.

The accepted repeats have zero runtime errors and empty stderr logs. Retaining
the complete first repeat and restarting the nonexistent second artifact is
methodologically equivalent to two complete independent executions.

### LFM direct-tools runtime failure

One LFM 8B direct-tools case reached the maximum tool-round limit in repeat 2.
It is recorded as infrastructure, not model failure, and that family is
excluded from direct-tools family comparison. Both full-harness repeats and
the primary no-tools controls completed without runtime errors.

### Luna telemetry

The repaired Luna-low adapter recorded full production composition for all 100
full-harness cases in each repeat. It captured 379 and 373 provider calls and
1,926.7 and 2,078.2 seconds of provider time, with zero runtime errors and
empty stderr logs.

## What the benchmark proves

Within this frozen task distribution and fixed configuration:

1. **Tools provide substantial capability.** Every model completed many more
   cases with direct tools than with the production prompt alone.
2. **Orchestration can add value beyond tool access.** This is clearest for
   Gemma and directionally present for LFM 8B and one Luna repeat.
3. **Harness value is not limited to small models.** Luna also improved when
   run through the harness.
4. **Model capacity still sets a floor.** LFM 1.2B improved many isolated
   cases but could not turn that gain into additional 4-of-4 family passes.
5. **A larger model is not automatically a faster product.** The hosted Luna
   path was much slower than local execution in this adapter and test setup.

## What the benchmark does not prove

- It does not show that tools or prompts changed model knowledge or reasoning
  capacity.
- It is not a public leaderboard or a general claim about every assistant
  task.
- It does not establish universal latency, energy, VRAM, or hosted cost.
- It does not make 100 cases equal to 100 independent samples; there are 25
  independent families.
- It does not fully separate every harness component. The direct-tools arm
  separates tool access from the whole orchestration bundle, but not routing,
  retry, permissions, and verification individually.
- It does not guarantee exact reproducibility from a hosted model.
- It does not justify tuning on this consumed bank and rerunning until a
  preferred result appears.

Model capacity, harness capability, and product quality remain separate
scorecards. Historical MMLU controls found no reproducible capacity increase
from the harness. A calculator, file reader, or verified state transition can
improve user outcomes without changing what the model knows closed-book.

## Publication and audit limits

The formal campaign analyzer currently reports `publish_ready: false`.
Remaining gaps are dominated by:

- missing explicit state-preflight evidence records;
- semantic-fallback attestations not populated for many lexical failures;
- no-action truthfulness evidence fields;
- model-isolation and model-load-time attestations; and
- causal autopsies for failed model/arm/family cells.

These omissions do not alter the deterministic score totals reported above,
but they limit causal diagnosis and prevent calling the campaign hole-proof.
The current result should be described as **strong engineering evidence with
explicit audit gaps**.

The next research-grade confirmation should use a fresh frozen bank with more
independent families after the evidence schema is complete. This consumed bank
should remain an audit artifact, not a tuning target.

## Historical 32-case development campaign

An earlier 32-case development suite first established the direction:

| Model | Raw -> full harness |
| --- | ---: |
| LFM 1.2B | 7/32 -> 18/32 |
| LFM 8B-A1B | 6/32 -> 24/32 |
| Gemma 26B-A4B | 8/32 -> 25/32 |
| Luna, high reasoning | 10/32 -> 27/32; 11/32 -> 23/32 |

Those results were useful for development, but several cases shared semantic
families and the equal-tools direct control was absent. They are preserved as
historical context only. The sealed four-arm, 25-family confirmation in this
report supersedes them for current conclusions.

## Reproduction record

The product repository intentionally contains no hidden expected answers,
scorer predicates, or private fixtures. The sibling private
`local-benchmark-runner` repository is authoritative for immutable run
artifacts and exact evaluator implementation.

Key evaluator records:

- `experiments/manifests/sealed-2026s3-lfm-confirmation-v1.yaml`
- `runs/sealed-2026s3-lfm-confirmation-v1/analysis/family-analysis.json`
- `experiments/manifests/sealed-2026s3-gemma26-confirmation-v1.yaml`
- `experiments/verdicts/sealed-2026s3-gemma26-confirmation-v1.md`
- `experiments/manifests/sealed-2026s3-luna-low-confirmation-v1.yaml`
- `experiments/verdicts/sealed-2026s3-luna-low-confirmation-v1.md`

Representative evaluator workflow:

```powershell
benchrun bank verify eval\banks\sealed-2026s3

benchrun bank compile-eval `
  --bank eval\banks\sealed-2026s3 `
  --base-config configs\sealed-2026s3-campaign-base.yml `
  --out <private-config> `
  --suite-name sealed-2026s3

benchrun eval run `
  --config <private-config> `
  --suite sealed-2026s3 `
  --provider <provider> `
  --label <label> `
  --mode raw `
  --mode same_prompt_direct `
  --mode same_prompt_tools_direct `
  --mode harness_full `
  --runs-dir <campaign>\scored `
  --max-case-evals 400 `
  --allow-large-campaign `
  --dry-run

# Execute the same command without --dry-run, then repeat unchanged.
benchrun bank analyze-campaign --campaign <campaign>
```

The exact accepted commands, hashes, run directories, and exclusions remain in
the evaluator manifests and verdicts rather than being reconstructed from this
public summary.
