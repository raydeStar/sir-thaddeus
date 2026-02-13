# Voice Engine Setup and Verification

## Engine Selection Precedence

- If `voice.ttsEngine` is unset, runtime defaults to `windows`.
- If `voice.sttEngine` is unset, runtime defaults to `faster-whisper`.
- `faster-whisper` defaults `sttModelId` to `base` when omitted.
- `voice.sttLanguage` defaults to `en` (use `"auto"` to disable pinning).
- `kokoro` requires `ttsVoiceId`.
- `qwen3asr` requires explicit `sttModelId`.

## Voice Turn Timing Contract

Each completed voice turn emits stage timestamps and summary metrics in audit events:

- Stage event action: `VOICE_STAGE_TIMESTAMP`
- Per-turn summary action: `VOICE_TURN_TIMING_SUMMARY`

Stage fields logged per turn:

- `t_mic_down`
- `t_first_audio_frame`
- `t_mic_up`
- `t_asr_start`
- `t_asr_first_token`
- `t_asr_final`
- `t_agent_start`
- `t_agent_final`
- `t_tts_start`
- `t_playback_start`

Computed fields logged per turn:

- `audio_capture_duration_ms = t_mic_up - t_mic_down`
- `asr_latency_ms = t_asr_final - t_asr_start`
- `end_to_end_to_playback_start_ms = t_playback_start - t_mic_down`

Notes:

- Preview ASR session IDs (`preview-*`) are excluded from final-turn summaries.
- If partial ASR tokens are unavailable, `t_asr_first_token` is set from the final response path.

## Realtime Transcription Path

Runtime now accumulates live preview transcript while PTT is held and submits that text as a realtime hint on mic release.

- Preview ASR runs during `Listening`.
- Preview chunks are merged into one rolling transcript (overlap-aware token merge).
- On mic-up, orchestrator can accept a fresh hint and skip the full post-release ASR pass.
- If hint is missing/stale/too short, runtime falls back to normal full ASR.

## Kokoro Model Files

### Automatic download (default)

When `ttsEngine=kokoro` and `ttsVoiceId` is set, the voice backend
auto-downloads missing model files on startup from GitHub releases.
No manual setup required — just configure and run.

Model variant defaults to `v1.0` (310 MB ONNX + 27 MB voice bundle).
Override via CLI or environment variable:

```powershell
# CLI
python server.py --tts-engine kokoro --tts-voice-id af_sky --kokoro-variant v1.0-fp16

# Environment variable
$env:KOKORO_MODEL_VARIANT = "v1.0-int8"
```

Available variants (see `model_registry.json` for full details):

| Variant      | Model Size | Notes                  |
|-------------|-----------|------------------------|
| `v1.0`      | ~310 MB   | Full precision (f32)   |
| `v1.0-fp16` | ~169 MB   | Half precision         |
| `v1.0-int8` |  ~88 MB   | Quantized, smallest    |
| `v0.19`     | ~310 MB   | Legacy, v0.x compat    |

Files land in `apps/voice-backend/voices/<voiceId>/` and are
`.gitignore`d — they never enter version control.

### Manual install (alternative)

If you prefer to supply your own model files:

1. Place artifacts in a source folder.
2. Run:

```powershell
./dev/install-kokoro-assets.ps1 -VoiceId af_sky -SourceDir "C:\path\to\kokoro-pack"
```

3. Configure settings:
   - `voice.ttsEngine = "kokoro"`
   - `voice.ttsVoiceId = "af_sky"`

## Smoke Commands

Backend health:

```powershell
curl.exe -sS "http://127.0.0.1:8001/health"
```

TTS smoke:

```powershell
python -c "import requests; r=requests.post('http://127.0.0.1:8001/tts/test', json={'text':'hello'}, timeout=10); print(r.status_code); print(r.text)"
```

STT smoke:

```powershell
python -c "import requests; r=requests.post('http://127.0.0.1:8001/stt/test', timeout=10); print(r.status_code); print(r.text)"
```

STT benchmark contract:

```powershell
python -c "import requests; r=requests.post('http://127.0.0.1:8001/stt/bench', timeout=10); print(r.status_code); print(r.text)"
```

## Benchmarks

Automated endpoint benchmark (backend `/tts/test` + `/stt/bench`):

```powershell
./dev/bench-asr.ps1 -BaseUrl "http://127.0.0.1:8001" -Runs 10 -SttLanguage "en"
```

Manual true-turn benchmark (desktop runtime audit log parser):

```powershell
./dev/bench-voice-turn.ps1 -RunsPerPhrase 10
```

Manual benchmark expectations:

- Run 10 utterances for each phrase in order:
  1. `Hello there.`
  2. `What is six times seven?`
  3. Long 6-8 second sentence.
- Script reads `%LOCALAPPDATA%/SirThaddeus/audit.jsonl`.
- Script reports median + p95 for `asr_latency_ms` and `end_to_end_to_playback_start_ms`.
