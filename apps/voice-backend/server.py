"""Local voice backend with deterministic STT/TTS engine contracts."""

from __future__ import annotations

import argparse
import base64
import hashlib
import inspect
import importlib
import importlib.metadata
import io
import json
import logging
import os
import struct
import tempfile
import threading
import time
import uuid
import wave
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple

from fastapi import FastAPI, File, Form, Request, UploadFile
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel


logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-5s  %(name)s  %(message)s",
    datefmt="%H:%M:%S",
)
logger = logging.getLogger("voice-backend")

app = FastAPI(title="Voice Backend", version="0.2.0")

SCHEMA_VERSION = 1
INSTANCE_ID = uuid.uuid4().hex
APP_VERSION = "0.2.0"
ROOT_DIR = Path(__file__).resolve().parent
VOICES_ROOT = ROOT_DIR / "voices"
STT_MODELS_ROOT = ROOT_DIR / "stt-models"
UNSAFE_ARTIFACTS_ALLOWED = (os.environ.get("ST_VOICE_ALLOW_UNSAFE_ARTIFACTS", "").strip().lower() in {"1", "true", "yes", "on"})

BLOCKED_ARTIFACT_EXTENSIONS = {".pt", ".pth"}
KOKORO_ALLOWED_EXTENSIONS = {
    ".onnx",
    ".json",
    ".txt",
    ".bin",
    ".safetensors",
    ".npy",
    ".wav",
}
STT_ALLOWED_EXTENSIONS = {
    ".onnx",
    ".json",
    ".txt",
    ".bin",
    ".safetensors",
    ".model",
    ".wav",
}


def utc_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def env_bool(name: str, default: bool) -> bool:
    raw = os.environ.get(name, "").strip().lower()
    if not raw:
        return default
    return raw in {"1", "true", "yes", "on"}


def normalize_tts_engine(value: Optional[str]) -> str:
    normalized = (value or "").strip().lower()
    if not normalized:
        return "windows"
    if normalized in {"windows", "kokoro"}:
        return normalized
    return normalized


def normalize_stt_engine(value: Optional[str]) -> str:
    normalized = (value or "").strip().lower()
    if not normalized:
        return "faster-whisper"
    if normalized == "whisper":
        return "faster-whisper"
    if normalized in {"faster-whisper", "qwen3asr"}:
        return normalized
    return normalized


def normalize_stt_language(value: Optional[str]) -> str:
    normalized = (value or "").strip().lower()
    if not normalized:
        return "en"
    if normalized in {"auto", "detect"}:
        return ""
    return normalized


def resolve_stt_model_id(engine: str, model_id: Optional[str]) -> str:
    candidate = (model_id or "").strip()
    if candidate:
        return candidate
    if engine == "faster-whisper":
        return "base"
    return ""


def resolve_request_id(prefix: str, *candidates: Optional[str]) -> str:
    for candidate in candidates:
        if candidate and candidate.strip():
            return candidate.strip()
    return f"{prefix}-{uuid.uuid4().hex}"


def parse_content_type(upload: Optional[UploadFile]) -> str:
    if upload is None:
        return "audio/wav"
    return (upload.content_type or "audio/wav").strip() or "audio/wav"


def current_working_set_mb() -> float:
    try:
        import psutil  # type: ignore

        rss = psutil.Process(os.getpid()).memory_info().rss
        return round(rss / (1024.0 * 1024.0), 2)
    except Exception:
        pass

    if os.name == "nt":
        try:
            import ctypes
            from ctypes import wintypes

            class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
                _fields_ = [
                    ("cb", wintypes.DWORD),
                    ("PageFaultCount", wintypes.DWORD),
                    ("PeakWorkingSetSize", ctypes.c_size_t),
                    ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t),
                    ("PeakPagefileUsage", ctypes.c_size_t),
                ]

            counters = PROCESS_MEMORY_COUNTERS()
            counters.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
            handle = ctypes.windll.kernel32.GetCurrentProcess()
            ok = ctypes.windll.psapi.GetProcessMemoryInfo(
                handle, ctypes.byref(counters), counters.cb
            )
            if ok:
                return round(counters.WorkingSetSize / (1024.0 * 1024.0), 2)
        except Exception:
            pass

    return 0.0


def audio_seconds_from_wav(data: bytes) -> float:
    try:
        with wave.open(io.BytesIO(data), "rb") as reader:
            frames = reader.getnframes()
            rate = reader.getframerate()
            if rate <= 0:
                return 0.0
            return frames / float(rate)
    except Exception:
        return 0.0


def hash_file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().lower()


def is_path_safe_relative(value: str) -> bool:
    if not value or value.startswith("/") or value.startswith("\\"):
        return False
    normalized = value.replace("\\", "/")
    return ".." not in normalized.split("/")


def extract_manifest_entries(manifest_data: Dict[str, Any]) -> List[Tuple[str, str]]:
    entries_raw = manifest_data.get("files", [])
    parsed: List[Tuple[str, str]] = []

    if isinstance(entries_raw, list):
        for item in entries_raw:
            if isinstance(item, str):
                parsed.append((item, ""))
                continue
            if isinstance(item, dict):
                rel = str(item.get("path") or item.get("file") or "").strip()
                sha = str(item.get("sha256") or "").strip().lower()
                if rel:
                    parsed.append((rel, sha))
    return parsed


@dataclass
class FileProbeResult:
    installed: bool
    missing: List[str] = field(default_factory=list)
    last_error: str = ""


@dataclass
class InitProbeResult:
    ready: bool
    startup_ms: int
    last_error: str = ""


def verify_manifest_bundle(
    bundle_dir: Path,
    allowed_extensions: Iterable[str],
    bundle_name: str,
) -> FileProbeResult:
    manifest_path = bundle_dir / "manifest.json"
    if not manifest_path.exists():
        return FileProbeResult(
            installed=False,
            missing=[f"{bundle_name}/manifest.json"],
            last_error="manifest_missing",
        )

    try:
        manifest_data = json.loads(manifest_path.read_text(encoding="utf-8"))
    except Exception as exc:
        return FileProbeResult(
            installed=False,
            missing=[f"{bundle_name}/manifest.json"],
            last_error=f"manifest_parse_error:{exc}",
        )

    entries = extract_manifest_entries(manifest_data)
    if not entries:
        return FileProbeResult(
            installed=False,
            missing=[f"{bundle_name}/manifest.json:files"],
            last_error="manifest_files_missing",
        )

    missing: List[str] = []
    allowed = {ext.lower() for ext in allowed_extensions}
    for rel, expected_sha in entries:
        if not is_path_safe_relative(rel):
            return FileProbeResult(
                installed=False,
                missing=[],
                last_error=f"manifest_path_invalid:{rel}",
            )

        ext = Path(rel).suffix.lower()
        if ext in BLOCKED_ARTIFACT_EXTENSIONS and not UNSAFE_ARTIFACTS_ALLOWED:
            return FileProbeResult(
                installed=False,
                missing=[],
                last_error=f"artifact_blocked:{rel}",
            )
        if ext and ext not in allowed:
            return FileProbeResult(
                installed=False,
                missing=[],
                last_error=f"artifact_extension_not_allowed:{rel}",
            )

        full_path = bundle_dir / rel
        if not full_path.exists():
            missing.append(str(Path(bundle_name) / rel).replace("\\", "/"))
            continue

        if expected_sha:
            try:
                actual_sha = hash_file_sha256(full_path)
            except Exception as exc:
                return FileProbeResult(
                    installed=False,
                    missing=[],
                    last_error=f"hash_read_failed:{rel}:{exc}",
                )
            if actual_sha != expected_sha:
                return FileProbeResult(
                    installed=False,
                    missing=[],
                    last_error=f"hash_mismatch:{rel}",
                )

    return FileProbeResult(installed=len(missing) == 0, missing=missing, last_error="")


class BaseProvider:
    def __init__(self, engine: str, model_id: str):
        self.engine = engine
        self.model_id = model_id
        self._init_lock = threading.Lock()
        self._init_cache: Optional[InitProbeResult] = None

    @property
    def requires_init_probe(self) -> bool:
        return True

    def file_probe(self) -> FileProbeResult:
        raise NotImplementedError

    def _run_init_probe(self) -> InitProbeResult:
        raise NotImplementedError

    def engine_version(self) -> str:
        return ""

    def device_name(self) -> str:
        return ""

    def get_cached_init_probe(self) -> Optional[InitProbeResult]:
        with self._init_lock:
            return self._init_cache

    def init_probe(self, force: bool = False) -> InitProbeResult:
        with self._init_lock:
            if self._init_cache is not None and not force:
                return self._init_cache

            file_probe = self.file_probe()
            if not file_probe.installed:
                self._init_cache = InitProbeResult(
                    ready=False,
                    startup_ms=0,
                    last_error=file_probe.last_error or "file_probe_failed",
                )
                return self._init_cache

            if not self.requires_init_probe:
                self._init_cache = InitProbeResult(ready=True, startup_ms=0, last_error="")
                return self._init_cache

            started = time.perf_counter()
            try:
                probe = self._run_init_probe()
            except Exception as exc:  # pragma: no cover - defensive
                probe = InitProbeResult(ready=False, startup_ms=0, last_error=str(exc))
            elapsed_ms = int((time.perf_counter() - started) * 1000.0)
            if probe.startup_ms <= 0:
                probe.startup_ms = elapsed_ms
            self._init_cache = probe
            return self._init_cache

    def build_engine_status(self, run_init_probe: bool) -> Dict[str, Any]:
        file_probe = self.file_probe()
        cached = self.get_cached_init_probe()
        if run_init_probe and (cached is None or not cached.ready):
            cached = self.init_probe(force=False)

        startup_ms = cached.startup_ms if cached else 0
        last_error = file_probe.last_error
        ready = file_probe.installed

        if self.requires_init_probe:
            if cached is None:
                ready = False
                if not last_error:
                    last_error = "init_probe_not_run"
            else:
                ready = ready and cached.ready
                if cached.last_error:
                    last_error = cached.last_error

        details_missing = list(file_probe.missing)
        return {
            "schemaVersion": SCHEMA_VERSION,
            "ready": bool(ready),
            "engine": self.engine,
            "engineVersion": self.engine_version(),
            "modelId": self.model_id,
            "instanceId": INSTANCE_ID,
            "timestampUtc": utc_now(),
            "details": {
                "installed": bool(file_probe.installed),
                "missing": details_missing,
                "lastError": last_error,
                "startupMs": startup_ms,
            },
        }


class UnsupportedTtsProvider(BaseProvider):
    def __init__(self, engine: str, model_id: str, voice_id: str):
        super().__init__(engine, model_id)
        self.voice_id = voice_id

    def file_probe(self) -> FileProbeResult:
        return FileProbeResult(
            installed=False,
            missing=[f"tts_engine:{self.engine}"],
            last_error=f"tts_engine_unsupported:{self.engine}",
        )

    def _run_init_probe(self) -> InitProbeResult:
        return InitProbeResult(ready=False, startup_ms=0, last_error=f"tts_engine_unsupported:{self.engine}")

    def synthesize(self, text: str, request_id: str) -> Tuple[bytes, int]:
        raise RuntimeError(f"TTS engine '{self.engine}' is not supported.")


class UnsupportedSttProvider(BaseProvider):
    def file_probe(self) -> FileProbeResult:
        return FileProbeResult(
            installed=False,
            missing=[f"stt_engine:{self.engine}"],
            last_error=f"stt_engine_unsupported:{self.engine}",
        )

    def _run_init_probe(self) -> InitProbeResult:
        return InitProbeResult(ready=False, startup_ms=0, last_error=f"stt_engine_unsupported:{self.engine}")

    def transcribe(self, audio_bytes: bytes, request_id: str) -> str:
        raise RuntimeError(f"STT engine '{self.engine}' is not supported.")


class WindowsTtsProvider(BaseProvider):
    def __init__(self):
        super().__init__("windows", "")

    @property
    def requires_init_probe(self) -> bool:
        return False

    def file_probe(self) -> FileProbeResult:
        return FileProbeResult(installed=True, missing=[], last_error="")

    def _run_init_probe(self) -> InitProbeResult:
        return InitProbeResult(ready=True, startup_ms=0, last_error="")

    def synthesize(self, text: str, request_id: str) -> Tuple[bytes, int]:
        raise RuntimeError("windows_engine_external_runtime")


def convert_audio_to_wav_bytes(audio_data: Any, sample_rate: int) -> bytes:
    if isinstance(audio_data, (bytes, bytearray)):
        data = bytes(audio_data)
        if data.startswith(b"RIFF"):
            return data
        # Assume raw PCM16 mono.
        out = io.BytesIO()
        with wave.open(out, "wb") as writer:
            writer.setnchannels(1)
            writer.setsampwidth(2)
            writer.setframerate(sample_rate)
            writer.writeframes(data)
        return out.getvalue()

    if hasattr(audio_data, "tolist"):
        audio_data = audio_data.tolist()

    if not isinstance(audio_data, (list, tuple)):
        raise RuntimeError("kokoro_audio_format_unrecognized")

    pcm_frames = bytearray()
    for sample in audio_data:
        value = sample
        if isinstance(sample, (list, tuple)):
            value = sample[0] if sample else 0.0
        value_f = float(value)
        if value_f > 1.0:
            value_f = 1.0
        if value_f < -1.0:
            value_f = -1.0
        pcm_frames.extend(struct.pack("<h", int(value_f * 32767.0)))

    output = io.BytesIO()
    with wave.open(output, "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(sample_rate)
        writer.writeframes(bytes(pcm_frames))
    return output.getvalue()


def unpack_audio_result(result: Any, fallback_sample_rate: int = 24000) -> Tuple[Any, int]:
    if isinstance(result, tuple) and len(result) >= 2:
        first, second = result[0], result[1]
        if isinstance(first, (int, float)) and not isinstance(second, (int, float)):
            return second, int(first)
        if isinstance(second, (int, float)):
            return first, int(second)
        return first, fallback_sample_rate
    return result, fallback_sample_rate


class KokoroProvider(BaseProvider):
    def __init__(self, model_id: str, voice_id: str):
        super().__init__("kokoro", model_id)
        self.voice_id = (voice_id or "").strip()
        self._runtime_kind = ""
        self._runtime: Any = None

    def file_probe(self) -> FileProbeResult:
        if not self.voice_id:
            return FileProbeResult(
                installed=False,
                missing=["voiceId"],
                last_error="tts_voice_id_required",
            )

        voice_dir = VOICES_ROOT / self.voice_id
        manifest_probe = verify_manifest_bundle(
            voice_dir, KOKORO_ALLOWED_EXTENSIONS, f"voices/{self.voice_id}"
        )
        if not manifest_probe.installed:
            return manifest_probe

        kind, err = self._detect_runtime()
        if not kind:
            return FileProbeResult(
                installed=False,
                missing=["python_package:kokoro_or_kokoro_onnx"],
                last_error=err or "kokoro_runtime_missing",
            )

        return FileProbeResult(installed=True, missing=[], last_error="")

    def engine_version(self) -> str:
        for package_name in ("kokoro-onnx", "kokoro_onnx", "kokoro"):
            try:
                return importlib.metadata.version(package_name)
            except Exception:
                continue
        return ""

    def _detect_runtime(self) -> Tuple[str, str]:
        try:
            module = importlib.import_module("kokoro_onnx")
            if hasattr(module, "Kokoro"):
                return "kokoro_onnx", ""
        except Exception:
            pass

        try:
            module = importlib.import_module("kokoro")
            if hasattr(module, "KPipeline"):
                return "kokoro", ""
        except Exception as exc:
            return "", str(exc)

        return "", "kokoro_runtime_not_found"

    def _resolve_model_and_voices_paths(self) -> Tuple[Optional[Path], Optional[Path]]:
        voice_dir = VOICES_ROOT / self.voice_id
        model_path: Optional[Path] = None
        voices_path: Optional[Path] = None

        # Prefer explicit model-id folder if present.
        if self.model_id:
            candidate_dir = STT_MODELS_ROOT / self.model_id
            if candidate_dir.exists():
                for path in candidate_dir.rglob("*.onnx"):
                    model_path = path
                    break

        if model_path is None:
            for path in voice_dir.rglob("*.onnx"):
                model_path = path
                break

        # kokoro-onnx loads voices via np.load, commonly a .bin/.npy/.npz bundle.
        for pattern in ("*.bin", "*.npy", "*.npz"):
            for path in voice_dir.rglob(pattern):
                voices_path = path
                break
            if voices_path is not None:
                break

        return model_path, voices_path

    def _load_runtime(self) -> None:
        if self._runtime is not None:
            return

        runtime_kind, err = self._detect_runtime()
        if not runtime_kind:
            raise RuntimeError(err or "kokoro_runtime_missing")

        self._runtime_kind = runtime_kind
        if runtime_kind == "kokoro_onnx":
            module = importlib.import_module("kokoro_onnx")
            model_path, voices_path = self._resolve_model_and_voices_paths()
            if model_path is None:
                raise RuntimeError("kokoro_model_onnx_missing")
            if voices_path is None:
                raise RuntimeError("kokoro_voices_bundle_missing")
            kokoro_cls = getattr(module, "Kokoro")
            try:
                self._runtime = kokoro_cls(str(model_path), str(voices_path))
            except TypeError:
                self._runtime = kokoro_cls(
                    model_path=str(model_path),
                    voices_path=str(voices_path),
                )
        else:
            module = importlib.import_module("kokoro")
            pipeline_cls = getattr(module, "KPipeline")
            lang_code = os.environ.get("KOKORO_LANG", "en-us")
            try:
                self._runtime = pipeline_cls(lang_code=lang_code)
            except TypeError:
                self._runtime = pipeline_cls(lang_code)

    def _synthesize_internal(self, text: str) -> Tuple[bytes, int]:
        self._load_runtime()

        if self._runtime_kind == "kokoro_onnx":
            try:
                result = self._runtime.create(text, voice=self.voice_id, speed=1.0, lang="en-us")
            except TypeError:
                result = self._runtime.create(text, voice=self.voice_id)
            audio_data, sample_rate = unpack_audio_result(result, fallback_sample_rate=24000)
            wav_bytes = convert_audio_to_wav_bytes(audio_data, sample_rate)
            return wav_bytes, sample_rate

        generator = self._runtime(text, voice=self.voice_id)
        chunk = next(iter(generator))
        audio_data = chunk[-1] if isinstance(chunk, (tuple, list)) else chunk
        sample_rate = int(getattr(self._runtime, "sample_rate", 24000))
        wav_bytes = convert_audio_to_wav_bytes(audio_data, sample_rate)
        return wav_bytes, sample_rate

    def _run_init_probe(self) -> InitProbeResult:
        try:
            wav_bytes, _sample_rate = self._synthesize_internal("Voice engine initialization check.")
            if len(wav_bytes) < 44:
                return InitProbeResult(ready=False, startup_ms=0, last_error="kokoro_synthesis_empty")
            return InitProbeResult(ready=True, startup_ms=0, last_error="")
        except Exception as exc:
            return InitProbeResult(ready=False, startup_ms=0, last_error=str(exc))

    def synthesize(self, text: str, request_id: str) -> Tuple[bytes, int]:
        probe = self.init_probe(force=False)
        if not probe.ready:
            raise RuntimeError(probe.last_error or "kokoro_not_ready")
        wav_bytes, sample_rate = self._synthesize_internal(text)
        logger.info("TTS [%s] kokoro voice=%s bytes=%d", request_id, self.voice_id, len(wav_bytes))
        return wav_bytes, sample_rate


class FasterWhisperProvider(BaseProvider):
    def __init__(self, model_id: str, device: str, language: str):
        super().__init__("faster-whisper", model_id)
        self._device = (device or "cpu").strip().lower() or "cpu"
        self._language = normalize_stt_language(language)
        self._model = None
        self._compute_type = "int8" if self._device == "cpu" else "float16"

    def engine_version(self) -> str:
        try:
            return importlib.metadata.version("faster-whisper")
        except Exception:
            return ""

    def device_name(self) -> str:
        return self._device

    def file_probe(self) -> FileProbeResult:
        try:
            importlib.import_module("faster_whisper")
        except Exception as exc:
            return FileProbeResult(
                installed=False,
                missing=["python_package:faster-whisper"],
                last_error=str(exc),
            )
        if not self.model_id:
            return FileProbeResult(
                installed=False,
                missing=["modelId"],
                last_error="stt_model_id_required",
            )
        return FileProbeResult(installed=True, missing=[], last_error="")

    def _run_init_probe(self) -> InitProbeResult:
        try:
            from faster_whisper import WhisperModel

            if self._model is None:
                logger.info(
                    "Loading faster-whisper model '%s' on %s (%s)...",
                    self.model_id,
                    self._device,
                    self._compute_type,
                )
                self._model = WhisperModel(
                    self.model_id,
                    device=self._device,
                    compute_type=self._compute_type,
                )
            return InitProbeResult(ready=True, startup_ms=0, last_error="")
        except Exception as exc:
            return InitProbeResult(ready=False, startup_ms=0, last_error=str(exc))

    def transcribe(self, audio_bytes: bytes, request_id: str) -> str:
        probe = self.init_probe(force=False)
        if not probe.ready:
            raise RuntimeError(probe.last_error or "faster_whisper_not_ready")

        if len(audio_bytes) < 100:
            return ""

        fd, temp_path = tempfile.mkstemp(suffix=".wav")
        try:
            with os.fdopen(fd, "wb") as fh:
                fh.write(audio_bytes)
            transcribe_kwargs: Dict[str, Any] = {
                "beam_size": 1,
                "condition_on_previous_text": False,
            }
            if self._language:
                transcribe_kwargs["language"] = self._language
            segments, _info = self._model.transcribe(temp_path, **transcribe_kwargs)
            text = " ".join(segment.text for segment in segments).strip()
            logger.info("ASR [%s] faster-whisper bytes=%d", request_id, len(audio_bytes))
            return text
        finally:
            try:
                os.unlink(temp_path)
            except OSError:
                pass


class Qwen3AsrProvider(BaseProvider):
    def __init__(self, model_id: str, device: str, language: str):
        super().__init__("qwen3asr", model_id)
        self._device = (device or "cpu").strip().lower() or "cpu"
        self._language = normalize_stt_language(language)
        self._runtime_model: Any = None

    def engine_version(self) -> str:
        try:
            return importlib.metadata.version("qwen-asr")
        except Exception:
            return ""

    def device_name(self) -> str:
        return self._device

    def _is_remote_model_id(self) -> bool:
        model_id = (self.model_id or "").strip()
        return "/" in model_id and not Path(model_id).exists()

    def _resolve_model_reference(self) -> str:
        model_id = (self.model_id or "").strip()
        if Path(model_id).exists():
            return str(Path(model_id).resolve())
        if self._is_remote_model_id():
            return model_id
        return str((STT_MODELS_ROOT / model_id).resolve())

    def file_probe(self) -> FileProbeResult:
        if not self.model_id:
            return FileProbeResult(
                installed=False,
                missing=["modelId"],
                last_error="stt_model_id_required",
            )

        for module_name in ("qwen_asr", "torch"):
            try:
                importlib.import_module(module_name)
            except Exception as exc:
                return FileProbeResult(
                    installed=False,
                    missing=[f"python_package:{module_name}"],
                    last_error=str(exc),
                )

        if self._is_remote_model_id():
            return FileProbeResult(installed=True, missing=[], last_error="")

        model_path = Path(self._resolve_model_reference())
        if model_path.is_dir():
            manifest_path = model_path / "manifest.json"
            if manifest_path.exists():
                return verify_manifest_bundle(
                    model_path,
                    STT_ALLOWED_EXTENSIONS,
                    f"stt-models/{self.model_id}",
                )
            return FileProbeResult(installed=True, missing=[], last_error="")

        if model_path.exists():
            return FileProbeResult(installed=True, missing=[], last_error="")

        return FileProbeResult(
            installed=False,
            missing=[f"stt-models/{self.model_id}/manifest.json"],
            last_error="manifest_missing",
        )

    def _run_init_probe(self) -> InitProbeResult:
        try:
            from qwen_asr import Qwen3ASRModel
            import torch

            if self._runtime_model is None:
                model_ref = self._resolve_model_reference()
                use_cuda = self._device != "cpu" and torch.cuda.is_available()
                dtype = torch.bfloat16 if use_cuda else torch.float32
                device_map = "cuda:0" if use_cuda else "cpu"
                logger.info(
                    "Loading qwen3asr model '%s' on %s...",
                    model_ref,
                    "cuda" if use_cuda else "cpu",
                )
                self._runtime_model = Qwen3ASRModel.from_pretrained(
                    model_ref,
                    dtype=dtype,
                    device_map=device_map,
                    max_inference_batch_size=8,
                    max_new_tokens=256,
                )

                # Tiny warmup run to verify inference path end-to-end.
                silence_wav = self._build_silence_wav(0.25, 16000)
                fd, path = tempfile.mkstemp(suffix=".wav")
                try:
                    with os.fdopen(fd, "wb") as fh:
                        fh.write(silence_wav)
                    _ = self._transcribe_model(path)
                finally:
                    try:
                        os.unlink(path)
                    except OSError:
                        pass

            return InitProbeResult(ready=True, startup_ms=0, last_error="")
        except Exception as exc:
            return InitProbeResult(ready=False, startup_ms=0, last_error=str(exc))

    def transcribe(self, audio_bytes: bytes, request_id: str) -> str:
        probe = self.init_probe(force=False)
        if not probe.ready:
            raise RuntimeError(probe.last_error or "qwen3asr_not_ready")

        if len(audio_bytes) < 100:
            return ""

        fd, temp_path = tempfile.mkstemp(suffix=".wav")
        try:
            with os.fdopen(fd, "wb") as fh:
                fh.write(audio_bytes)
            result = self._transcribe_model(temp_path)
            text = ""
            if isinstance(result, list) and len(result) > 0:
                first = result[0]
                text = str(getattr(first, "text", "") or "").strip()
            elif result is not None:
                text = str(result).strip()
            logger.info("ASR [%s] qwen3asr bytes=%d", request_id, len(audio_bytes))
            return text
        finally:
            try:
                os.unlink(temp_path)
            except OSError:
                pass

    def _transcribe_model(self, audio_path: str) -> Any:
        kwargs: Dict[str, Any] = {"audio": audio_path}
        try:
            signature = inspect.signature(self._runtime_model.transcribe)
            parameters = signature.parameters
            if self._language and "language" in parameters:
                kwargs["language"] = self._language
            if "beam_size" in parameters:
                kwargs["beam_size"] = 1
        except Exception:
            if self._language:
                kwargs["language"] = self._language

        try:
            return self._runtime_model.transcribe(**kwargs)
        except TypeError:
            if "language" in kwargs:
                kwargs.pop("language", None)
                return self._runtime_model.transcribe(**kwargs)
            raise

    @staticmethod
    def _build_silence_wav(seconds: float, sample_rate: int) -> bytes:
        frame_count = max(1, int(seconds * sample_rate))
        samples = b"\x00\x00" * frame_count
        output = io.BytesIO()
        with wave.open(output, "wb") as writer:
            writer.setnchannels(1)
            writer.setsampwidth(2)
            writer.setframerate(sample_rate)
            writer.writeframes(samples)
        return output.getvalue()


def create_tts_provider(engine: str, model_id: str, voice_id: str) -> BaseProvider:
    normalized = normalize_tts_engine(engine)
    if normalized == "windows":
        return WindowsTtsProvider()
    if normalized == "kokoro":
        return KokoroProvider(model_id=model_id, voice_id=voice_id)
    return UnsupportedTtsProvider(normalized, model_id, voice_id)


def create_stt_provider(engine: str, model_id: str, device: str, language: str) -> BaseProvider:
    normalized = normalize_stt_engine(engine)
    if normalized == "faster-whisper":
        return FasterWhisperProvider(model_id=model_id, device=device, language=language)
    if normalized == "qwen3asr":
        return Qwen3AsrProvider(model_id=model_id, device=device, language=language)
    return UnsupportedSttProvider(normalized, model_id)


@dataclass(frozen=True)
class RuntimeConfig:
    port: int = 8001
    stt_engine: str = "faster-whisper"
    stt_model_id: str = "base"
    stt_language: str = "en"
    stt_device: str = "cpu"
    tts_engine: str = "windows"
    tts_model_id: str = ""
    tts_voice_id: str = ""


def build_runtime_config(
    port: Optional[int] = None,
    stt_engine: Optional[str] = None,
    stt_model_id: Optional[str] = None,
    stt_language: Optional[str] = None,
    stt_device: Optional[str] = None,
    tts_engine: Optional[str] = None,
    tts_model_id: Optional[str] = None,
    tts_voice_id: Optional[str] = None,
) -> RuntimeConfig:
    resolved_stt_engine = normalize_stt_engine(stt_engine or os.environ.get("ST_VOICE_STT_ENGINE") or os.environ.get("WHISPER_ENGINE"))
    resolved_stt_model = resolve_stt_model_id(
        resolved_stt_engine,
        stt_model_id or os.environ.get("ST_VOICE_STT_MODEL_ID") or os.environ.get("WHISPER_MODEL"),
    )
    resolved_stt_language = normalize_stt_language(
        stt_language or os.environ.get("ST_VOICE_STT_LANGUAGE") or "en"
    )
    resolved_tts_engine = normalize_tts_engine(tts_engine or os.environ.get("ST_VOICE_TTS_ENGINE"))
    resolved_tts_model = (tts_model_id or os.environ.get("ST_VOICE_TTS_MODEL_ID") or "").strip()
    resolved_tts_voice = (tts_voice_id or os.environ.get("ST_VOICE_TTS_VOICE_ID") or "").strip()
    resolved_device = (stt_device or os.environ.get("WHISPER_DEVICE") or "cpu").strip().lower() or "cpu"
    resolved_port = port or int(os.environ.get("PORT", "8001"))

    return RuntimeConfig(
        port=resolved_port,
        stt_engine=resolved_stt_engine,
        stt_model_id=resolved_stt_model,
        stt_language=resolved_stt_language,
        stt_device=resolved_device,
        tts_engine=resolved_tts_engine,
        tts_model_id=resolved_tts_model,
        tts_voice_id=resolved_tts_voice,
    )


class ProviderRegistry:
    def __init__(self, runtime_config: RuntimeConfig):
        self.runtime_config = runtime_config
        self._lock = threading.Lock()
        self._tts_cache: Dict[Tuple[str, str, str], BaseProvider] = {}
        self._stt_cache: Dict[Tuple[str, str, str], BaseProvider] = {}

    def get_tts(
        self,
        engine: Optional[str] = None,
        model_id: Optional[str] = None,
        voice_id: Optional[str] = None,
    ) -> BaseProvider:
        resolved_engine = normalize_tts_engine(engine or self.runtime_config.tts_engine)
        resolved_model = (model_id or self.runtime_config.tts_model_id or "").strip()
        resolved_voice = (voice_id or self.runtime_config.tts_voice_id or "").strip()
        key = (resolved_engine, resolved_model, resolved_voice)
        with self._lock:
            provider = self._tts_cache.get(key)
            if provider is None:
                provider = create_tts_provider(resolved_engine, resolved_model, resolved_voice)
                self._tts_cache[key] = provider
            return provider

    def get_stt(
        self,
        engine: Optional[str] = None,
        model_id: Optional[str] = None,
        language: Optional[str] = None,
    ) -> BaseProvider:
        resolved_engine = normalize_stt_engine(engine or self.runtime_config.stt_engine)
        resolved_model = resolve_stt_model_id(
            resolved_engine, model_id or self.runtime_config.stt_model_id
        )
        resolved_language = normalize_stt_language(
            self.runtime_config.stt_language if language is None else language
        )
        key = (resolved_engine, resolved_model, resolved_language)
        with self._lock:
            provider = self._stt_cache.get(key)
            if provider is None:
                provider = create_stt_provider(
                    resolved_engine,
                    resolved_model,
                    self.runtime_config.stt_device,
                    resolved_language,
                )
                self._stt_cache[key] = provider
            return provider


RUNTIME_CONFIG = build_runtime_config()
PROVIDERS = ProviderRegistry(RUNTIME_CONFIG)


class TtsRequest(BaseModel):
    text: str
    requestId: Optional[str] = None
    engine: Optional[str] = None
    modelId: Optional[str] = None
    voiceId: Optional[str] = None
    voice: Optional[str] = "default"
    format: Optional[str] = "pcm_s16le"
    sampleRate: Optional[int] = 24000


class TtsTestRequest(BaseModel):
    text: Optional[str] = None
    requestId: Optional[str] = None
    engine: Optional[str] = None
    modelId: Optional[str] = None
    voiceId: Optional[str] = None
    forceInit: bool = False


def build_health_payload(asr_status: Dict[str, Any], tts_status: Dict[str, Any]) -> Dict[str, Any]:
    asr_ready = bool(asr_status.get("ready", False))
    tts_ready = bool(tts_status.get("ready", False))
    ready = asr_ready and tts_ready

    error_code = ""
    message = ""
    asr_error = str(((asr_status.get("details") or {}).get("lastError") or "")).strip()
    tts_error = str(((tts_status.get("details") or {}).get("lastError") or "")).strip()
    if not ready:
        if not asr_ready and not tts_ready:
            error_code = "asr_tts_not_ready"
            message = f"ASR not ready: {asr_error or 'unknown'}; TTS not ready: {tts_error or 'unknown'}"
        elif not asr_ready:
            error_code = "asr_not_ready"
            message = f"ASR not ready: {asr_error or 'unknown'}"
        else:
            error_code = "tts_not_ready"
            message = f"TTS not ready: {tts_error or 'unknown'}"

    return {
        "schemaVersion": SCHEMA_VERSION,
        "instanceId": INSTANCE_ID,
        "timestampUtc": utc_now(),
        "status": "ok" if ready else "loading",
        "ready": ready,
        "asrReady": asr_ready,
        "ttsReady": tts_ready,
        "version": APP_VERSION,
        "errorCode": error_code,
        "message": message,
        "asr": asr_status,
        "tts": tts_status,
    }


def provider_unavailable_response(
    request_id: str,
    status: Dict[str, Any],
    surface: str,
    status_code: int = 503,
) -> JSONResponse:
    payload = {
        "error": f"{surface} unavailable.",
        "errorCode": f"{surface.lower()}_unavailable",
        "requestId": request_id,
        "engineStatus": status,
        "message": ((status.get("details") or {}).get("lastError") or ""),
    }
    return JSONResponse(payload, status_code=status_code, headers={"X-Request-Id": request_id})


@app.on_event("startup")
async def on_startup() -> None:
    # FileProbe + InitProbe warm-up for selected providers.
    try:
        stt_provider = PROVIDERS.get_stt()
        stt_provider.init_probe(force=False)
    except Exception as exc:
        logger.error("STT init probe failed on startup: %s", exc)

    try:
        tts_provider = PROVIDERS.get_tts()
        if tts_provider.engine != "windows":
            tts_provider.init_probe(force=False)
    except Exception as exc:
        logger.error("TTS init probe failed on startup: %s", exc)


@app.get("/health")
def health() -> Dict[str, Any]:
    asr_status = PROVIDERS.get_stt().build_engine_status(run_init_probe=False)
    tts_status = PROVIDERS.get_tts().build_engine_status(run_init_probe=False)
    return build_health_payload(asr_status, tts_status)


@app.post("/asr")
async def asr(
    request: Request,
    audio: Optional[UploadFile] = File(None),
    file: Optional[UploadFile] = File(None),
    sessionId: Optional[str] = Form(None),
    requestId: Optional[str] = Form(None),
    engine: Optional[str] = Form(None),
    modelId: Optional[str] = Form(None),
    language: Optional[str] = Form(None),
):
    request_id = resolve_request_id("asr", requestId, request.headers.get("X-Request-Id"))
    upload = audio or file
    if upload is None:
        return JSONResponse(
            {
                "error": "Missing 'audio' or 'file' multipart field.",
                "requestId": request_id,
            },
            status_code=400,
            headers={"X-Request-Id": request_id},
        )

    audio_bytes = await upload.read()
    provider = PROVIDERS.get_stt(engine=engine, model_id=modelId, language=language)
    init = provider.init_probe(force=False)
    status = provider.build_engine_status(run_init_probe=False)
    if not init.ready or not status.get("ready", False):
        return provider_unavailable_response(request_id, status, "STT")

    try:
        transcript = provider.transcribe(audio_bytes, request_id)
    except Exception as exc:
        status = provider.build_engine_status(run_init_probe=False)
        status["details"]["lastError"] = str(exc)
        return provider_unavailable_response(request_id, status, "STT")

    logger.info(
        "ASR [%s] session=%s bytes=%d transcript_chars=%d",
        request_id,
        (sessionId or "-"),
        len(audio_bytes),
        len(transcript),
    )
    return JSONResponse(
        {"text": transcript, "requestId": request_id},
        headers={"X-Request-Id": request_id},
    )


@app.post("/tts")
async def tts(payload: TtsRequest, request: Request):
    request_id = resolve_request_id("tts", payload.requestId, request.headers.get("X-Request-Id"))
    if not payload.text or not payload.text.strip():
        return JSONResponse(
            {"error": "Field 'text' is required.", "requestId": request_id},
            status_code=400,
            headers={"X-Request-Id": request_id},
        )

    resolved_voice = (
        payload.voiceId
        or (payload.voice if payload.voice and payload.voice != "default" else None)
    )
    provider = PROVIDERS.get_tts(
        engine=payload.engine,
        model_id=payload.modelId,
        voice_id=resolved_voice,
    )
    init = provider.init_probe(force=False)
    status = provider.build_engine_status(run_init_probe=False)
    if not init.ready or not status.get("ready", False):
        return provider_unavailable_response(request_id, status, "TTS")

    try:
        wav_bytes, sample_rate = provider.synthesize(payload.text.strip(), request_id)
    except Exception as exc:
        status = provider.build_engine_status(run_init_probe=False)
        status["details"]["lastError"] = str(exc)
        return provider_unavailable_response(request_id, status, "TTS")

    headers = {
        "X-Sample-Rate": str(sample_rate),
        "X-Channels": "1",
        "X-Format": (payload.format or "pcm_s16le"),
        "X-Request-Id": request_id,
    }
    return Response(
        content=wav_bytes,
        media_type="audio/wav",
        headers=headers,
        status_code=200,
    )


@app.post("/tts/test")
async def tts_test(payload: TtsTestRequest, request: Request):
    request_id = resolve_request_id("tts-test", payload.requestId, request.headers.get("X-Request-Id"))
    provider = PROVIDERS.get_tts(
        engine=payload.engine,
        model_id=payload.modelId,
        voice_id=payload.voiceId,
    )
    provider.init_probe(force=payload.forceInit)
    status = provider.build_engine_status(run_init_probe=False)
    if not status.get("ready", False):
        return provider_unavailable_response(request_id, status, "TTS")

    text = (payload.text or "Speech engine check.").strip()
    if not text:
        text = "Speech engine check."

    try:
        wav_bytes, sample_rate = provider.synthesize(text, request_id)
    except Exception as exc:
        status = provider.build_engine_status(run_init_probe=False)
        status["details"]["lastError"] = str(exc)
        return provider_unavailable_response(request_id, status, "TTS")

    return JSONResponse(
        {
            "ok": True,
            "requestId": request_id,
            "engine": provider.engine,
            "modelId": provider.model_id,
            "sampleRate": sample_rate,
            "bytes": len(wav_bytes),
            "audioBase64": base64.b64encode(wav_bytes).decode("ascii"),
            "engineStatus": status,
        },
        headers={"X-Request-Id": request_id},
    )


@app.post("/stt/test")
async def stt_test(
    request: Request,
    audio: Optional[UploadFile] = File(None),
    file: Optional[UploadFile] = File(None),
    requestId: Optional[str] = Form(None),
    engine: Optional[str] = Form(None),
    modelId: Optional[str] = Form(None),
    language: Optional[str] = Form(None),
):
    request_id = resolve_request_id("stt-test", requestId, request.headers.get("X-Request-Id"))
    provider = PROVIDERS.get_stt(engine=engine, model_id=modelId, language=language)
    provider.init_probe(force=False)
    status = provider.build_engine_status(run_init_probe=False)
    if not status.get("ready", False):
        return provider_unavailable_response(request_id, status, "STT")

    upload = audio or file
    if upload is None:
        return JSONResponse(
            {
                "ok": True,
                "requestId": request_id,
                "engine": provider.engine,
                "modelId": provider.model_id,
                "engineStatus": status,
            },
            headers={"X-Request-Id": request_id},
        )

    audio_bytes = await upload.read()
    transcript = provider.transcribe(audio_bytes, request_id)
    return JSONResponse(
        {
            "ok": True,
            "requestId": request_id,
            "engine": provider.engine,
            "modelId": provider.model_id,
            "contentType": parse_content_type(upload),
            "bytes": len(audio_bytes),
            "transcript": transcript,
            "engineStatus": status,
        },
        headers={"X-Request-Id": request_id},
    )


@app.post("/stt/bench")
async def stt_bench(
    request: Request,
    audio: Optional[UploadFile] = File(None),
    file: Optional[UploadFile] = File(None),
    requestId: Optional[str] = Form(None),
    engine: Optional[str] = Form(None),
    modelId: Optional[str] = Form(None),
    language: Optional[str] = Form(None),
):
    request_id = resolve_request_id("stt-bench", requestId, request.headers.get("X-Request-Id"))
    upload = audio or file
    if upload is None:
        return JSONResponse(
            {"error": "Missing 'audio' or 'file' multipart field.", "requestId": request_id},
            status_code=400,
            headers={"X-Request-Id": request_id},
        )

    provider = PROVIDERS.get_stt(engine=engine, model_id=modelId, language=language)
    provider.init_probe(force=False)
    status = provider.build_engine_status(run_init_probe=False)
    if not status.get("ready", False):
        return provider_unavailable_response(request_id, status, "STT")

    audio_bytes = await upload.read()
    audio_seconds = audio_seconds_from_wav(audio_bytes)
    started = time.perf_counter()
    transcript = provider.transcribe(audio_bytes, request_id)
    wall_ms = int((time.perf_counter() - started) * 1000.0)
    rtf = (wall_ms / 1000.0) / audio_seconds if audio_seconds > 0 else 0.0
    startup_ms = int(((status.get("details") or {}).get("startupMs") or 0))

    return JSONResponse(
        {
            "requestId": request_id,
            "engine": provider.engine,
            "modelId": provider.model_id,
            "audioSeconds": round(audio_seconds, 3),
            "wallMs": wall_ms,
            "rtf": round(rtf, 4),
            "startupMs": startup_ms,
            "processWorkingSetMb": current_working_set_mb(),
            "device": provider.device_name(),
            "transcript": transcript,
        },
        headers={"X-Request-Id": request_id},
    )


@app.post("/shutdown")
async def shutdown():
    logger.info("Shutdown requested.")

    def _exit_later():
        time.sleep(0.2)
        os._exit(0)

    threading.Thread(target=_exit_later, daemon=True).start()
    return {"ok": True, "message": "Shutting down"}


if __name__ == "__main__":
    import uvicorn

    parser = argparse.ArgumentParser(description="Local voice backend for deterministic ASR/TTS")
    parser.add_argument("--port", type=int, default=8001, help="Listen port (default: 8001)")
    parser.add_argument("--stt-engine", type=str, default=None, help="STT engine (faster-whisper|qwen3asr)")
    parser.add_argument("--stt-model-id", type=str, default=None, help="STT model id")
    parser.add_argument("--stt-language", type=str, default=None, help="Pinned STT language (default: en, use auto for detect)")
    parser.add_argument("--device", type=str, default=None, help="Whisper compute device: cpu or cuda")
    parser.add_argument("--tts-engine", type=str, default=None, help="TTS engine (windows|kokoro)")
    parser.add_argument("--tts-model-id", type=str, default=None, help="TTS model id")
    parser.add_argument("--tts-voice-id", type=str, default=None, help="TTS voice id")
    parser.add_argument("--kokoro-variant", type=str, default=None,
                        help="Kokoro model variant from model_registry.json (default: v1.0). "
                             "Options: v1.0, v1.0-fp16, v1.0-int8, v0.19")
    cli = parser.parse_args()

    RUNTIME_CONFIG = build_runtime_config(
        port=cli.port,
        stt_engine=cli.stt_engine,
        stt_model_id=cli.stt_model_id,
        stt_language=cli.stt_language,
        stt_device=cli.device,
        tts_engine=cli.tts_engine,
        tts_model_id=cli.tts_model_id,
        tts_voice_id=cli.tts_voice_id,
    )

    # ── Auto-download missing model files before providers initialize ──
    if RUNTIME_CONFIG.tts_engine == "kokoro" and RUNTIME_CONFIG.tts_voice_id:
        try:
            from model_downloader import ensure_kokoro_models

            registry_path = ROOT_DIR / "model_registry.json"
            variant = cli.kokoro_variant or os.environ.get("KOKORO_MODEL_VARIANT") or None
            ensure_kokoro_models(
                voices_root=VOICES_ROOT,
                voice_id=RUNTIME_CONFIG.tts_voice_id,
                registry_path=registry_path,
                variant=variant,
            )
        except Exception as exc:
            # Non-fatal: log the failure but let the server attempt startup.
            # If the files truly don't exist, the provider will fail later
            # with a clear error about the missing model.
            logger.warning("Model auto-download failed (non-fatal): %s", exc)

    PROVIDERS = ProviderRegistry(RUNTIME_CONFIG)

    logger.info("Starting voice backend on 127.0.0.1:%d", RUNTIME_CONFIG.port)
    logger.info(
        "STT=%s model=%s lang=%s device=%s | TTS=%s model=%s voice=%s",
        RUNTIME_CONFIG.stt_engine,
        RUNTIME_CONFIG.stt_model_id,
        RUNTIME_CONFIG.stt_language or "auto",
        RUNTIME_CONFIG.stt_device,
        RUNTIME_CONFIG.tts_engine,
        RUNTIME_CONFIG.tts_model_id or "<none>",
        RUNTIME_CONFIG.tts_voice_id or "<none>",
    )
    logger.info(
        "Unsafe artifact toggle: %s",
        "enabled" if UNSAFE_ARTIFACTS_ALLOWED else "disabled",
    )

    uvicorn.run(app, host="127.0.0.1", port=RUNTIME_CONFIG.port, log_level="info")
