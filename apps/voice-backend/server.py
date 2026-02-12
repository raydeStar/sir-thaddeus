"""
Minimal local voice backend for ASR and TTS.

Serves /health, /asr (via faster-whisper), and /tts (stub) on a single port.
The desktop runtime's AudioPlaybackService falls back to Windows SAPI when
the TTS endpoint returns empty audio, so the stub is intentional — it keeps
the VoiceHost health check happy without requiring a full local TTS engine.

Usage:
    python server.py [--port 8001] [--model base] [--device cpu]

Environment variables:
    WHISPER_MODEL   Whisper model size (default: "base")
    WHISPER_DEVICE  Compute device: "cpu" or "cuda" (default: "cpu")
"""

import io
import logging
import os
import sys
import tempfile
import threading
import time
from typing import Optional

# ── Logging ──────────────────────────────────────────────────────

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-5s  %(name)s  %(message)s",
    datefmt="%H:%M:%S",
)
logger = logging.getLogger("voice-backend")

# ── FastAPI App ──────────────────────────────────────────────────

from fastapi import FastAPI, File, Form, Request, UploadFile
from fastapi.responses import JSONResponse, Response

app = FastAPI(title="Voice Backend", version="0.1.0")

# ── ASR Model (lazy-loaded on first request) ─────────────────────

_asr_model = None
_asr_model_name: str = os.environ.get("WHISPER_MODEL", "base")
_asr_device: str = os.environ.get("WHISPER_DEVICE", "cpu")
_asr_ready: bool = False


def _load_asr_model():
    """Load the Whisper model once, on first /asr request."""
    global _asr_model, _asr_ready

    from faster_whisper import WhisperModel

    compute_type = "int8" if _asr_device == "cpu" else "float16"
    logger.info(
        "Loading Whisper model '%s' on %s (%s)...",
        _asr_model_name,
        _asr_device,
        compute_type,
    )
    _asr_model = WhisperModel(
        _asr_model_name, device=_asr_device, compute_type=compute_type
    )
    _asr_ready = True
    logger.info("Whisper model loaded and ready.")


# ── Startup Event ────────────────────────────────────────────────


@app.on_event("startup")
async def on_startup():
    """Eagerly load the model so /health returns ready=true quickly."""
    try:
        _load_asr_model()
    except Exception as exc:
        logger.error("Failed to load ASR model on startup: %s", exc)
        logger.info("Model will be retried on first /asr request.")


# ── Health ───────────────────────────────────────────────────────


@app.get("/health")
def health():
    """
    VoiceHost probes this for both ASR and TTS readiness.
    Returns ready=true once the Whisper model is loaded.
    """
    return {"ready": _asr_ready, "status": "ok" if _asr_ready else "loading"}


# ── ASR (Speech-to-Text) ────────────────────────────────────────


@app.post("/asr")
async def asr(
    audio: Optional[UploadFile] = File(None),
    file: Optional[UploadFile] = File(None),
    sessionId: Optional[str] = Form(None),
):
    """
    Accepts a WAV file via multipart form-data (field 'audio' or 'file'),
    runs Whisper transcription, and returns {"text": "..."}.
    """
    global _asr_model, _asr_ready

    upload = audio or file
    if upload is None:
        return JSONResponse(
            {"error": "Missing 'audio' or 'file' multipart field."},
            status_code=400,
        )

    data = await upload.read()
    if len(data) < 100:
        return {"text": ""}

    # Lazy model load if startup failed
    if _asr_model is None:
        try:
            _load_asr_model()
        except Exception as exc:
            logger.error("ASR model load failed: %s", exc)
            return JSONResponse(
                {"error": f"ASR model unavailable: {exc}"}, status_code=503
            )

    # Write to temp file (faster-whisper needs a file path)
    fd, tmp_path = tempfile.mkstemp(suffix=".wav")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)

        segments, info = _asr_model.transcribe(tmp_path, beam_size=5, language="en")
        text = " ".join(seg.text for seg in segments).strip()
    finally:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass

    logger.info("ASR [%s] (%d bytes) → %s", sessionId or "-", len(data), text[:120])
    return {"text": text}


# ── TTS (Text-to-Speech Stub) ───────────────────────────────────


@app.post("/tts")
async def tts(request: Request):
    """
    Stub endpoint. Returns an empty response with audio/wav content type.
    The desktop runtime detects empty audio and falls back to Windows SAPI,
    so a full local TTS engine is not required for the basic voice loop.

    When a real TTS backend (Kokoro, etc.) is added later, replace this stub.
    """
    return Response(content=b"", media_type="audio/wav", status_code=200)


@app.post("/shutdown")
async def shutdown():
    """
    Best-effort graceful shutdown hook used by VoiceHost supervisor.
    Return the response first, then terminate shortly after.
    """
    logger.info("Shutdown requested.")

    def _exit_later():
        time.sleep(0.2)
        os._exit(0)

    threading.Thread(target=_exit_later, daemon=True).start()
    return {"ok": True, "message": "Shutting down"}


# ── CLI Entry Point ──────────────────────────────────────────────

if __name__ == "__main__":
    import argparse

    import uvicorn

    parser = argparse.ArgumentParser(description="Local voice backend for ASR/TTS")
    parser.add_argument("--port", type=int, default=8001, help="Listen port (default: 8001)")
    parser.add_argument("--model", type=str, default=None, help="Whisper model size (default: base)")
    parser.add_argument("--device", type=str, default=None, help="Compute device: cpu or cuda")
    cli = parser.parse_args()

    if cli.model:
        _asr_model_name = cli.model
    if cli.device:
        _asr_device = cli.device

    logger.info("Starting voice backend on 127.0.0.1:%d", cli.port)
    logger.info("ASR model: %s  device: %s", _asr_model_name, _asr_device)

    uvicorn.run(app, host="127.0.0.1", port=cli.port, log_level="info")
