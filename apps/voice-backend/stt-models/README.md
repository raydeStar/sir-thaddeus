# STT Model Packs

Optional local model bundles can be placed here:

- `stt-models/<modelId>/manifest.json`
- `stt-models/<modelId>/<artifacts...>`

Manifest format:

```json
{
  "files": [
    { "path": "encoder.onnx", "sha256": "<sha256>" },
    { "path": "config.json", "sha256": "<sha256>" }
  ]
}
```

Allowed extensions: `.onnx`, `.json`, `.txt`, `.bin`, `.safetensors`, `.model`, `.wav`.

Blocked by default: `.pt`, `.pth` (unless unsafe mode is explicitly enabled).

Current rollout status:

- `faster-whisper` remains default STT engine.
- `qwen3asr` provider is scaffolded and health-visible, but runtime is intentionally not promoted yet.
