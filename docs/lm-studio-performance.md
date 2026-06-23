# LM Studio performance settings

Sir Thaddeus now warms LM Studio at startup, serializes local LLM calls, trims oversized prompts, and exposes `/health/llm`. The remaining smoothness work happens in LM Studio and Windows.

## Recommended LM Studio settings

1. Enable **Limit Model Offload to Dedicated GPU Memory**.
2. Set **Max Concurrent Predictions** to `1`.
3. Use a modest daily-driver context length by default: `4096`, or `8192` if VRAM remains stable.
4. Enable **Flash Attention** when the selected model/backend supports it.
5. Prefer one always-loaded daily-driver model instead of loading large models on demand.
6. Watch Windows Task Manager while testing:
   - Dedicated GPU memory
   - Shared GPU memory
   - CPU usage
   - Disk usage
7. If shared GPU memory rises during inference, reduce:
   - context length
   - model size
   - GPU offload
   - KV cache GPU usage

## Optional startup script

Set `$ModelKey` to the exact LM Studio model key from `lms ls` before using this.

```powershell
$ModelKey = "<MODEL_KEY_HERE>"
$Identifier = "sir-thaddeus"
$ContextLength = 4096

lms server start
lms load $ModelKey --identifier $Identifier --context-length $ContextLength --gpu max
```

Do not assume the placeholder model key is valid. If the model load causes desktop stutter, lower `$ContextLength`, choose a smaller daily-driver model, or reduce GPU offload.

## Verification

1. Start the runtime and open `/health/llm`.
2. Confirm `warmupCompleted` becomes `true` when LM Studio is running.
3. Confirm `activeRequests` never exceeds `1` with the default settings.
4. Send a large prompt and check logs for `llm.prompt_reduced`.
5. Confirm the app still starts and reports degraded health when LM Studio is offline.
