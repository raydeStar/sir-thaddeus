# Raw LLM vs AI agent harness: sealed 100-case evidence

This July 2026 campaign tested whether the unchanged Sir Thaddeus AI agent
harness improves verified task completion for models ranging from a 1.2B local
LLM to hosted Luna. It is an answer-blind product-repository summary. The
sibling private evaluator repository remains authoritative for case data, raw
responses, scorer predicates, run-directory hashes, and exact commands.

## Verdict

**The harness improved paired case completion for all four models, including
Luna. Gemma 4 26B-A4B also produced a statistically clear improvement on the
predeclared strict family metric in both repeats.**

This is strong engineering evidence, not a claim that tools changed model
weights or closed-book intelligence. The formal campaign is not yet
publication-ready because several evidence contracts and per-family failure
autopsies remain incomplete.

## Benchmark design

- 100 private, human-reviewed cases with fabricated entities and evidence;
- 25 semantic families, each represented by four mutations;
- two complete repeats per model and arm;
- exact deterministic scoring or independently observed final state;
- no LLM judge in the primary score;
- temperature 0, 8,192-token context, and a 512-token output ceiling;
- one model loaded at a time, with model identity and quantization recorded;
- bank SHA-256 `89c96cda7e484453fc60a103d8b3d183dbaa9380c1fdf40ffc4b1c4b3718ecbd`;
- product commit `3315c6c2ba10e9f05cdaf338d91de8f7cfb54476`.

The four paired arms were:

1. `raw`: a minimal answer-only prompt with no tools;
2. `production_prompt_no_tools`: the real Sir Thaddeus production prompt,
   personality, and response contract, without tools;
3. `same_prompt_tools_direct`: the same production prompt with direct access
   to the declared tools, but without the full orchestration loop; and
4. `harness_full`: the unchanged production harness with tools, permissions,
   retries, verification, and state handling.

The primary comparison was full harness versus production prompt without tools.
The primary metric was strict 4-of-4 family completion. Case completion was a
predeclared secondary metric because four mutations from one family are not
four independent research samples.

## Results

Each cell shows repeat 1 / repeat 2. Identical values are shown once.

| Model | Raw families; cases | Same prompt, no tools | Direct tools | Full harness | Full minus no-tools families |
| --- | ---: | ---: | ---: | ---: | ---: |
| LFM 2.5 1.2B Q4_K_M | 2/25; 15/100 | 0/25; 13/100 | 2/25; 32/100 | 2/25; 33/100 | +2 / +2 |
| LFM 2.5 8B-A1B Q4_K_M | 4/25; 24/100 | 4/25; 22/100 | 7/25; 44/100 | 6/25; 47/100 / 46/100 | +2 / +2 |
| Gemma 4 26B-A4B Q4_K_M | 4/25; 23/100 | 1/25; 17/100 | 4/25; 51/100 | 12/25 / 11/25; 58/100 | +11 / +10 |
| Luna, high reasoning | 4/25; 22/100 | 5/25; 23/100 / 22/100 | Inconclusive: two setup failures per repeat | 9/25 / 12/25; 55/100 / 65/100 | +4 / +7 |

### Primary family-level inference

| Model | Repeat | Family W/L/T | Delta | Bootstrap 95% CI | Exact sign p |
| --- | ---: | ---: | ---: | ---: | ---: |
| LFM 1.2B | 1 / 2 | 2/0/23 | +8 points | 0 to +20 | 0.5000 |
| LFM 8B-A1B | 1 / 2 | 3/1/21 | +8 points | -8 to +24 | 0.6250 |
| Gemma 26B-A4B | 1 | 11/0/14 | +44 points | +24 to +64 | 0.00098 |
| Gemma 26B-A4B | 2 | 10/0/15 | +40 points | +20 to +60 | 0.00195 |
| Luna | 1 | 4/0/21 | +16 points | +4 to +32 | 0.1250 |
| Luna | 2 | 7/0/18 | +28 points | +12 to +48 | 0.01563 |

Only Gemma is statistically clear on the primary metric in both repeats. Luna
is directionally positive in both but highly variable. The two LFM strict-family
deltas are positive but not resolved by a bank with only 25 independent
families.

### Secondary paired case evidence

| Model | Repeat | Case W/L/T | Exact paired sign p |
| --- | ---: | ---: | ---: |
| LFM 1.2B | 1 / 2 | 24/4/72 | 0.000180 |
| LFM 8B-A1B | 1 | 28/3/69 | 0.00000465 |
| LFM 8B-A1B | 2 | 27/3/70 | 0.00000843 |
| Gemma 26B-A4B | 1 / 2 | 45/4/51 | 0.000000000823 |
| Luna | 1 | 34/2/64 | 0.0000000194 |
| Luna | 2 | 43/0/57 | 0.000000000000227 |

These paired case results strongly favor the harness, but the cases are nested
mutations. They support the engineering conclusion and do not replace the
family-level primary analysis.

## Repeat stability

| Model | Raw pass agreement | No-tools agreement | Direct-tools agreement | Full-harness agreement | Full family agreement |
| --- | ---: | ---: | ---: | ---: | ---: |
| LFM 1.2B | 100/100 | 100/100 | 100/100 | 100/100 | 25/25 |
| LFM 8B-A1B | 100/100 | 100/100 | 98/100 | 95/100 | 25/25 |
| Gemma 26B-A4B | 100/100 | 100/100 | 100/100 | 96/100 | 24/25 |
| Luna | 100/100 | 99/100 | 89/100 | 84/100 | 20/25 |

Temperature zero did not make hosted Luna deterministic. Luna changed 16 full
harness pass/fail outcomes between repeats. LFM 1.2B reproduced every outcome,
although some tool-capable text and trace details still changed.

## Speed, calls, tokens, and VRAM

Each cell shows full-harness repeat 1 / repeat 2.

| Model | Sum of case latency | Model calls | Tokens | Peak VRAM |
| --- | ---: | ---: | ---: | ---: |
| LFM 1.2B | 481.5s / 482.1s | 257 / 257 | 217,032 / 216,904 | 2,907 / 2,854 MiB |
| LFM 8B-A1B | 622.3s / 624.8s | 291 / 292 | 362,904 / 362,271 | 7,368 / 7,364 MiB |
| Gemma 26B-A4B | 564.1s / 609.5s | 303 / 305 | 337,037 / 338,286 | 20,797 / 20,776 MiB |
| Luna | 4,013.3s / 3,696.0s | not emitted by this adapter | 446,288 / 415,987 | hosted; not applicable |

The two complete four-arm grids took about 24 minutes for LFM 1.2B, 39 minutes
for LFM 8B-A1B, 33 minutes for Gemma 26B-A4B, and 209 minutes for Luna on this
Windows/RTX 4090 test host. These are execution-path measurements, not universal
provider benchmarks.

Gemma is the practical standout in this campaign: 58/100 full-harness outcomes
in both repeats, statistically clear family lift, and roughly one-sixth of
Luna's summed full-harness latency. Luna reached a higher second-run ceiling,
but was far slower and much less stable.

## What tools explain

- LFM 1.2B: direct tools reached 32/100 and full harness 33/100. Most lift came
  from capability access; full orchestration added little on this bank.
- LFM 8B-A1B: direct tools reached 44/100 and full harness 47/100 / 46/100.
  Full orchestration improved cases but not the strict family count.
- Gemma 26B-A4B: direct tools reached 51/100 and 4/25 strict families; full
  harness reached 58/100 and 12/25 / 11/25. This is the clearest evidence that
  orchestration and verification added value beyond merely giving the model tools.
- Luna: direct-tools attribution is invalid because the same two Wiki SQLite
  setup failures occurred before model evaluation in both repeats.

## Exclusions and limits

Two complete 400-evaluation attempts were retained but excluded:

1. An initial LFM 1.2B run exposed a Python-to-C# state-serialization defect.
   Sixteen direct-tools cases failed preflight before model evaluation.
2. An initial Luna run exposed Windows default stdin encoding. Six non-ASCII
   raw prompts failed before reaching Luna. The repair passed focused tests and
   a real non-bank Unicode sentinel before the two accepted reruns.

The formal analyzer currently reports `publish_ready: false`. It found no bank
hash, run-directory hash, model-identity, sampling, or repository-cleanliness
drift, but it also identified missing evidence:

- semantic fallback judgments were not run after hundreds of lexical failures;
- 100 full-harness stateful records lacked explicit initial-state preflight
  telemetry, although their final states were independently observed;
- 28 clarification rows lacked the analyzer's expected lexical metric;
- the repeated Luna SQLite setup failures omitted four state and prompt records;
- 329 failed model/arm/family cells have not received causal autopsy.

Those gaps do not change the deterministic score totals, but they do mean this
is not yet a hole-proof research proof. The honest label is **hash-bound,
repeat-tested engineering evidence**.

## What this answers

Yes, a harness can improve a frontier-class model. Luna's full harness beat the
same Luna production prompt without tools in both repeats. The result also
shows why “use the largest model” is not the whole deployment strategy: a
well-harnessed local Gemma model approached Luna's verified completion with much
lower latency and better repeat stability on this task distribution.

The next confirmation should fix full-harness preflight telemetry and Wiki
SQLite setup, then use a new sealed bank with more independent semantic
families. This consumed bank must not be tuned and rerun until it passes.

## Reproduction pointer

The private evaluator record is
`experiments/verdicts/sealed-2026s3-four-arm-campaign-v1.md` in the sibling
`local-benchmark-runner` repository. It points to the predeclared protocol,
accepted/excluded run hashes, private family analysis, and exact commands.
