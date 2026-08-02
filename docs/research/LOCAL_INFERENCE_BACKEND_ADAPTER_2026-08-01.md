# Local inference backend adapter decision

**Verdict:** adopt native `llama-server` as the first managed research backend,
retain LM Studio as the backward-compatible default, and treat Ollama or any
other OpenAI-compatible server as an externally managed endpoint. This is a
research-infrastructure decision, not a benchmark uplift or a production model
change.

## Why this seam

Sir Thaddeus already sends product traffic through one OpenAI-compatible client
construction boundary. The remaining provider lock-in was
`dev/model-intake.ps1`: it called LM Studio's `lms` CLI directly to discover and
load a model. That made the benchmark harness depend on the desktop application
even though the runtime itself only needed an HTTP endpoint.

The adapter now divides responsibilities explicitly:

```text
model-intake.ps1
    |
    +-- lmstudio  -> lms load -> existing endpoint (backward compatible)
    |
    +-- llamacpp  -> exact llama-server.exe + exact GGUF -> loopback endpoint
    |                 owns and stops only the process it started
    |
    `-- external  -> existing OpenAI-compatible endpoint
                      never owns provider lifecycle

all three -> temporary settings -> unchanged headless runtime -> unchanged suites/scoring
```

The provider adapter is evaluation tooling. Desktop and headless product hosts
continue to use the shared `LlmClientFactory` boundary and do not learn how to
launch a research server.

## Evidence and selection

The selection was made from current official documentation on 2026-08-01:

- The official llama.cpp server documents native Windows binaries, direct GGUF
  loading, OpenAI-compatible chat and model endpoints, parallel decoding,
  metrics, and a router mode. Its function-calling guide documents the
  tool-aware Jinja-template requirement. These are the smallest set of features
  needed for the current Windows/GGUF research loop.
  [Server documentation](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md),
  [function-calling documentation](https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md)
- Ollama officially supports OpenAI-compatible chat, tools, `tool_choice`,
  deterministic seeds, GGUF import, Windows service operation, concurrency, and
  keep-alive controls. It remains a good externally managed comparison, but its
  import/model lifecycle is an additional layer when the research artifact is
  already a GGUF file.
  [OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility),
  [GGUF import](https://docs.ollama.com/import),
  [Windows](https://docs.ollama.com/windows)
- vLLM's current quickstart identifies Linux as the supported OS and describes
  its OpenAI-compatible server and generation-config behavior. SGLang likewise
  exposes rich serving, parser, batching, and metrics controls but is primarily
  a Linux/WSL deployment choice. Both remain strong later candidates for a
  throughput or multi-user server campaign, not the first native-Windows
  single-model adapter.
  [vLLM quickstart](https://docs.vllm.ai/en/latest/getting_started/quickstart/),
  [vLLM OpenAI server](https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/),
  [SGLang server arguments](https://github.com/sgl-project/sglang/blob/main/docs/advanced_features/server_arguments.md)

The conclusion is an engineering inference from those capabilities and the
repository's current Windows/GGUF workflow. It is not a claim that llama.cpp is
universally faster or more accurate. Backend throughput, tool-call validity,
and task outcomes still require paired measurements with a frozen model,
quantization, context, prompt, sampling configuration, and item set.

## Deterministic contract

`dev/ModelProviderAdapter.psm1` owns provider preparation:

- `lmstudio` preserves `lms load` and probes the configured `/v1/models` route;
- `llamacpp` validates exact executable and GGUF paths, binds to
  `127.0.0.1`, supplies a stable model alias, aligns provider and runtime
  context, enables `--jinja` and `--metrics`, and probes `/v1/models`;
- the managed native path launches one model, requires the gatekeeper alias to
  match it, and rejects a port that already has a responding provider so the
  script cannot claim ownership of someone else's server;
- `external` requires an explicit HTTP(S) base URL, probes it, and never starts
  or stops a process;
- every live native llama.cpp run hashes both `llama-server` and the GGUF;
- context, GPU offload, and parallel-slot controls are recorded and applied to
  both managed backends; an explicitly controlled LM Studio arm requires a
  fresh load instead of trusting unknown resident settings;
- LM Studio models loaded by intake are unloaded during cleanup, while
  pre-existing loaded models are never unloaded by the adapter;
- native stdout/stderr, the exact argument vector, endpoint, readiness time,
  PID, settings hash, and artifact hashes are retained in
  `provider-plan.json`;
- artifact directories include both model and backend identity so paired arms
  cannot overwrite one another even when they start in the same second;
- managed llama.cpp defaults to loopback port `18080` rather than llama.cpp's
  upstream `8080` default, because Sir Thaddeus already reserves `8080` for
  SearXNG; settings generation also rejects an explicit authority collision;
- cleanup targets only the captured process object. No name-based or broad
  process termination is used.

The temporary runtime settings set provider identity, endpoint, model alias,
shared gatekeeper endpoint, temperature zero, and the exact native context.
Existing suites, expected answers, scorers, thresholds, production pipeline,
permissions, and response contracts are not changed.

## Use

Validate a native plan without launching anything or calling a model:

```powershell
./dev/model-intake.ps1 `
  -Backend llamacpp `
  -ModelId gemma-4-12b-it-q4_k_xl `
  -LlamaServerPath C:\tools\llama.cpp\llama-server.exe `
  -ModelPath D:\models\gemma-4-12b-it-q4_k_xl.gguf `
  -ContextWindowTokens 16384 `
  -GpuOffload max `
  -Parallel 1 `
  -PlanOnly
```

Run the existing intake battery with a managed native server:

```powershell
./dev/model-intake.ps1 `
  -Backend llamacpp `
  -ModelId gemma-4-12b-it-q4_k_xl `
  -LlamaServerPath C:\tools\llama.cpp\llama-server.exe `
  -ModelPath D:\models\gemma-4-12b-it-q4_k_xl.gguf `
  -ContextWindowTokens 16384 `
  -GpuOffload max `
  -Parallel 1 `
  -Suites python-probe,solver-probe `
  -Repeats 3
```

Use an existing Ollama server without giving the script lifecycle ownership:

```powershell
./dev/model-intake.ps1 `
  -Backend external `
  -ProviderName ollama `
  -BaseUrl http://127.0.0.1:11434 `
  -ModelId gemma3:4b
```

An actual benchmark run is still subject to `docs/EXPERIMENTATION.md`: declare
the scorecard and budget first, use unchanged controls, reject on the cheap
slice, and do not infer an uplift from provider substitution alone.

## Promotion and next gate

This adapter can merge after deterministic contract tests, the repository test
gate, and protected CI pass. That promotes reproducible infrastructure only.

The first backend comparison is a separate campaign. It should keep one exact
GGUF and product SHA fixed and compare LM Studio with native llama.cpp on:

1. strict verified task outcomes and valid tool calls;
2. exact request and provider-call counts;
3. p50/p95 provider and end-to-end latency after a warm sentinel;
4. peak VRAM/RAM and startup/load time;
5. repeat stability and any template/parser differences.

Only that campaign can establish whether the backend produces a tooling,
orchestration, latency, or resource uplift.
