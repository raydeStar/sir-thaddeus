# Kokoro Voice Packs

Place each voice pack in its own folder:

- `voices/<voiceId>/manifest.json`
- `voices/<voiceId>/<artifacts...>`

Manifest format:

```json
{
  "voiceId": "af_sky",
  "generatedAtUtc": "2026-02-13T00:00:00Z",
  "files": [
    { "path": "model.onnx", "sha256": "<sha256>" },
    { "path": "voices.bin", "sha256": "<sha256>" }
  ]
}
```

Allowed extensions: `.onnx`, `.json`, `.txt`, `.bin`, `.safetensors`, `.npy`, `.wav`.

Blocked by default: `.pt`, `.pth` (unless unsafe mode is explicitly enabled).

Use `./dev/install-kokoro-assets.ps1` to generate or refresh `manifest.json`.
