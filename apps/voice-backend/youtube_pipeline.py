"""User-invoked YouTube ingest/transcribe/summarize job pipeline.

This module intentionally does not schedule background work on startup.
Jobs are only created by explicit API requests.
"""

from __future__ import annotations

import json
import logging
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import uuid
from collections import deque
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def utc_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def _int_env(name: str, default: int, min_value: int, max_value: int) -> int:
    raw = os.environ.get(name, "").strip()
    if not raw:
        return default
    try:
        parsed = int(raw)
    except ValueError:
        return default
    return max(min_value, min(max_value, parsed))


def _float_env(name: str, default: float, min_value: float, max_value: float) -> float:
    raw = os.environ.get(name, "").strip()
    if not raw:
        return default
    try:
        parsed = float(raw)
    except ValueError:
        return default
    return max(min_value, min(max_value, parsed))


def _truncate_text(text: str, max_chars: int) -> tuple[str, bool]:
    if len(text) <= max_chars:
        return text, False
    return text[:max_chars], True


def _normalize_draft_tone(raw: str) -> str:
    normalized = (raw or "").strip().lower()
    if normalized in {"professional", "playful", "direct"}:
        return normalized
    return "professional"


def _sanitize_folder_component(raw: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9_-]", "_", raw or "")
    cleaned = cleaned.strip("_")
    return cleaned[:96] if cleaned else "unknown"


def _is_youtube_url(url: str) -> bool:
    lowered = (url or "").strip().lower()
    if not lowered.startswith("http://") and not lowered.startswith("https://"):
        return False
    return "youtube.com/" in lowered or "youtu.be/" in lowered


class PipelineError(Exception):
    def __init__(self, code: str, message: str, details: Optional[Dict[str, Any]] = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.details = details or {}


@dataclass
class YouTubeSummaryConfig:
    base_url: str
    model: str
    temperature: float
    max_input_chars: int
    timeout_sec: int


@dataclass
class YouTubeJob:
    job_id: str
    video_url: str
    language_hint: str
    keep_audio: bool
    asr_engine: str
    asr_model: str
    summary_config: YouTubeSummaryConfig
    draft_tone: str = "professional"
    status: str = "Queued"
    stage: str = "Resolving"
    progress: float = 0.0
    created_at_utc: str = field(default_factory=utc_now)
    updated_at_utc: str = field(default_factory=utc_now)
    created_ts: float = field(default_factory=time.time, repr=False)
    updated_ts: float = field(default_factory=time.time, repr=False)
    output_dir: str = ""
    transcript_path: str = ""
    summary_path: str = ""
    hooks_path: str = ""
    facts_sheet_path: str = ""
    linkedin_carousel_path: str = ""
    x_thread_path: str = ""
    newsletter_summary_path: str = ""
    video_id: str = ""
    title: str = ""
    channel: str = ""
    duration_sec: int = 0
    summary: Optional[str] = None
    error: Optional[Dict[str, Any]] = None
    cancel_requested: bool = False
    cancel_event: threading.Event = field(default_factory=threading.Event, repr=False)
    active_process: Optional[subprocess.Popen[str]] = field(default=None, repr=False)


class YouTubeJobManager:
    def __init__(
        self,
        data_root: Path,
        transcribe_callback: Callable[[bytes, str, str, str, str], str],
        logger: logging.Logger,
    ) -> None:
        self._data_root = Path(data_root)
        self._youtube_root = self._data_root / "youtube"
        self._transcribe_callback = transcribe_callback
        self._logger = logger

        self._max_concurrent = _int_env("ST_YOUTUBE_MAX_CONCURRENT_JOBS", 1, 1, 4)
        self._max_history = _int_env("ST_YOUTUBE_JOB_HISTORY_MAX", 100, 10, 5000)
        self._ttl_seconds = _int_env("ST_YOUTUBE_JOB_TTL_SECONDS", 24 * 60 * 60, 300, 7 * 24 * 60 * 60)
        self._stdout_stderr_max_chars = _int_env("ST_YOUTUBE_LOG_CAPTURE_MAX_CHARS", 12000, 1000, 200000)
        self._download_timeout_sec = _int_env("ST_YOUTUBE_DOWNLOAD_TIMEOUT_SEC", 20 * 60, 60, 3 * 60 * 60)
        self._convert_timeout_sec = _int_env("ST_YOUTUBE_CONVERT_TIMEOUT_SEC", 20 * 60, 60, 3 * 60 * 60)
        self._asr_timeout_sec = _int_env("ST_YOUTUBE_ASR_TIMEOUT_SEC", 60 * 60, 60, 6 * 60 * 60)
        self._summary_timeout_sec = _int_env("ST_YOUTUBE_SUMMARY_TIMEOUT_SEC", 120, 10, 1800)

        self._jobs: Dict[str, YouTubeJob] = {}
        self._order: deque[str] = deque()
        self._lock = threading.Lock()
        self._semaphore = threading.Semaphore(self._max_concurrent)

        self._youtube_root.mkdir(parents=True, exist_ok=True)

    def dependency_status(self) -> Dict[str, Any]:
        yt_dlp_command = self._resolve_yt_dlp_command()
        ffmpeg_command = self._resolve_ffmpeg_command()
        yt_dlp_path = " ".join(yt_dlp_command)
        ffmpeg_path = ffmpeg_command[0] if ffmpeg_command else ""
        ready = bool(yt_dlp_command and ffmpeg_command)
        return {
            "ready": ready,
            "ytDlp": {"available": bool(yt_dlp_path), "path": yt_dlp_path or ""},
            "ffmpeg": {"available": bool(ffmpeg_path), "path": ffmpeg_path or ""},
            "dataRoot": str(self._youtube_root),
            "maxConcurrentJobs": self._max_concurrent,
        }

    @staticmethod
    def _resolve_env_tool_path(env_var: str) -> str:
        candidate = (os.environ.get(env_var) or "").strip()
        if not candidate:
            return ""
        expanded = os.path.expandvars(os.path.expanduser(candidate))
        return expanded if os.path.isfile(expanded) else ""

    @staticmethod
    def _can_run_command(args: list[str]) -> bool:
        try:
            completed = subprocess.run(
                args,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                text=True,
                timeout=8,
                check=False,
            )
            return completed.returncode == 0
        except Exception:
            return False

    def _resolve_yt_dlp_command(self) -> list[str]:
        yt_dlp_path = self._resolve_env_tool_path("ST_YOUTUBE_YTDLP_PATH") or (shutil.which("yt-dlp") or "")
        if yt_dlp_path:
            return [yt_dlp_path]

        python_exe = (sys.executable or "").strip()
        if python_exe and self._can_run_command([python_exe, "-m", "yt_dlp", "--version"]):
            return [python_exe, "-m", "yt_dlp"]

        return []

    def _resolve_ffmpeg_command(self) -> list[str]:
        ffmpeg_path = self._resolve_env_tool_path("ST_YOUTUBE_FFMPEG_PATH") or (shutil.which("ffmpeg") or "")
        return [ffmpeg_path] if ffmpeg_path else []

    def start_job(
        self,
        video_url: str,
        language_hint: str,
        keep_audio: bool,
        asr_engine: str,
        asr_model: str,
        summary_config: YouTubeSummaryConfig,
        draft_tone: str = "professional",
    ) -> Dict[str, Any]:
        if not video_url or not video_url.strip():
            raise PipelineError("INVALID_URL", "videoUrl is required.")
        if not _is_youtube_url(video_url):
            raise PipelineError("INVALID_URL", "videoUrl must be a valid YouTube URL.")
        if not asr_model or not asr_model.strip():
            raise PipelineError("ASR_MODEL_UNAVAILABLE", "ASR model id/path is required.")

        job = YouTubeJob(
            job_id=f"ytjob-{uuid.uuid4().hex}",
            video_url=video_url.strip(),
            language_hint=(language_hint or "").strip(),
            keep_audio=bool(keep_audio),
            asr_engine=(asr_engine or "").strip(),
            asr_model=asr_model.strip(),
            summary_config=summary_config,
            draft_tone=_normalize_draft_tone(draft_tone),
            stage="Resolving",
            progress=0.0,
        )

        with self._lock:
            self._evict_locked()
            self._jobs[job.job_id] = job
            self._order.append(job.job_id)

        worker = threading.Thread(
            target=self._run_job_worker,
            args=(job.job_id,),
            name=f"youtube-job-{job.job_id}",
            daemon=True,
        )
        worker.start()
        return self._to_response(job)

    def get_job(self, job_id: str) -> Optional[Dict[str, Any]]:
        with self._lock:
            self._evict_locked()
            job = self._jobs.get(job_id)
            if job is None:
                return None
            return self._to_response(job)

    def cancel_job(self, job_id: str) -> Optional[Dict[str, Any]]:
        with self._lock:
            job = self._jobs.get(job_id)
            if job is None:
                return None

            if self._is_terminal(job):
                return self._to_response(job)

            job.cancel_requested = True
            job.cancel_event.set()
            self._kill_active_process_locked(job)

            if job.status == "Queued":
                self._mark_cancelled_locked(job, "Cancelled before execution started.")
            else:
                job.updated_ts = time.time()
                job.updated_at_utc = utc_now()
            return self._to_response(job)

    def _run_job_worker(self, job_id: str) -> None:
        job = self._get_job_internal(job_id)
        if job is None:
            return

        slot_acquired = False
        try:
            while not job.cancel_event.is_set():
                if self._semaphore.acquire(timeout=0.25):
                    slot_acquired = True
                    break

            if not slot_acquired:
                self._mark_cancelled(job, "Job cancelled while waiting for execution slot.")
                return

            self._set_stage(job, "Resolving", 0.05, status="Running")
            self._execute_pipeline(job)
        except PipelineError as exc:
            if exc.code == "JOB_CANCELLED":
                self._mark_cancelled(job, exc.message)
            else:
                self._mark_failed(job, exc.code, exc.message, exc.details)
        except Exception as exc:
            self._mark_failed(
                job,
                "ASR_TRANSCRIBE_FAILED",
                "Unexpected pipeline failure.",
                {"message": str(exc)},
            )
        finally:
            if slot_acquired:
                self._semaphore.release()
            self._detach_process(job)

    def _execute_pipeline(self, job: YouTubeJob) -> None:
        dep = self.dependency_status()
        if not dep["ready"]:
            raise PipelineError(
                "DEPENDENCY_MISSING",
                "Required tools are missing. Install yt-dlp and ffmpeg.",
                dep,
            )
        yt_dlp_command = self._resolve_yt_dlp_command()
        ffmpeg_command = self._resolve_ffmpeg_command()
        if not yt_dlp_command or not ffmpeg_command:
            raise PipelineError(
                "DEPENDENCY_MISSING",
                "Required tools are missing. Install yt-dlp and ffmpeg.",
                dep,
            )

        metadata = self._resolve_video_metadata(job, yt_dlp_command)
        video_id = _sanitize_folder_component(str(metadata.get("id") or ""))
        if not video_id:
            raise PipelineError("INVALID_URL", "Unable to resolve a single YouTube video id.")

        title = str(metadata.get("title") or "").strip()
        channel = str(metadata.get("uploader") or metadata.get("channel") or "").strip()
        duration_sec = int(metadata.get("duration") or 0)

        job.video_id = video_id
        job.title = title
        job.channel = channel
        job.duration_sec = max(0, duration_sec)

        output_dir = (self._youtube_root / video_id).resolve()
        work_dir = output_dir / "work"
        output_dir.mkdir(parents=True, exist_ok=True)
        work_dir.mkdir(parents=True, exist_ok=True)

        transcript_path = output_dir / "transcript.txt"
        summary_path = output_dir / "summary.txt"
        hooks_path = output_dir / "hooks.json"
        facts_sheet_path = output_dir / "facts_sheet.json"
        linkedin_carousel_path = output_dir / "linkedin_carousel.md"
        x_thread_path = output_dir / "x_thread.txt"
        newsletter_summary_path = output_dir / "newsletter_summary.md"
        metadata_path = output_dir / "metadata.json"
        job.output_dir = str(output_dir)
        job.transcript_path = str(transcript_path)
        job.summary_path = str(summary_path)
        job.hooks_path = str(hooks_path)
        job.facts_sheet_path = str(facts_sheet_path)
        job.linkedin_carousel_path = str(linkedin_carousel_path)
        job.x_thread_path = str(x_thread_path)
        job.newsletter_summary_path = str(newsletter_summary_path)

        self._write_metadata(
            metadata_path,
            job,
            extra={
                "summaryPath": str(summary_path),
                "hooksPath": str(hooks_path),
                "factsSheetPath": str(facts_sheet_path),
                "linkedinCarouselPath": str(linkedin_carousel_path),
                "xThreadPath": str(x_thread_path),
                "newsletterSummaryPath": str(newsletter_summary_path),
                "draftTone": job.draft_tone,
            },
        )

        self._set_stage(job, "DownloadingAudio", 0.12)
        source_template = work_dir / "source.%(ext)s"
        self._run_command(
            job,
            yt_dlp_command + ["-f", "bestaudio", "--no-playlist", "-o", str(source_template), job.video_url],
            failure_code="YOUTUBE_DOWNLOAD_FAILED",
            failure_message="yt-dlp failed to download audio.",
            timeout_sec=self._download_timeout_sec,
        )
        source_files = [p for p in work_dir.glob("source.*") if p.is_file()]
        if not source_files:
            raise PipelineError(
                "YOUTUBE_DOWNLOAD_FAILED",
                "yt-dlp completed but no source audio file was produced.",
            )
        source_files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
        source_path = source_files[0]

        self._set_stage(job, "ConvertingAudio", 0.20)
        audio_wav_path = work_dir / "audio.wav"
        self._run_command(
            job,
            ffmpeg_command + ["-y", "-i", str(source_path), "-ar", "16000", "-ac", "1", str(audio_wav_path)],
            failure_code="AUDIO_CONVERT_FAILED",
            failure_message="ffmpeg failed to convert audio to 16k mono wav.",
            timeout_sec=self._convert_timeout_sec,
        )
        if not audio_wav_path.exists():
            raise PipelineError(
                "AUDIO_CONVERT_FAILED",
                "Converted audio file was not produced.",
            )

        self._set_stage(job, "Transcribing", 0.35)
        if job.cancel_event.is_set():
            raise PipelineError("JOB_CANCELLED", "Job cancelled by user.")

        try:
            audio_bytes = audio_wav_path.read_bytes()
        except Exception as exc:
            raise PipelineError("IO_WRITE_FAILED", "Unable to read converted audio.", {"message": str(exc)}) from exc

        request_id = f"{job.job_id}-asr"
        transcript_text = self._transcribe_callback(
            audio_bytes,
            job.asr_engine,
            job.asr_model,
            job.language_hint,
            request_id,
        )
        transcript_text = (transcript_text or "").strip()

        self._set_stage(job, "WritingTranscript", 0.38)
        try:
            transcript_path.write_text(transcript_text + ("\n" if transcript_text else ""), encoding="utf-8")
        except Exception as exc:
            raise PipelineError("IO_WRITE_FAILED", "Failed to write transcript.txt.", {"message": str(exc)}) from exc

        self._set_stage(job, "ExtractingHooks", 0.55)
        hooks_data = self._extract_hooks(job, transcript_text)
        facts_sheet = self._build_facts_sheet(job, hooks_data)
        try:
            hooks_path.write_text(
                json.dumps(hooks_data, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8",
            )
            facts_sheet_path.write_text(
                json.dumps(facts_sheet, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8",
            )
        except Exception as exc:
            raise PipelineError(
                "IO_WRITE_FAILED",
                "Failed to write hooks.json or facts_sheet.json.",
                {"message": str(exc)},
            ) from exc

        self._set_stage(job, "GeneratingDrafts", 0.80)
        linkedin_carousel, x_thread, newsletter_summary = self._generate_drafts(job, hooks_data)

        self._set_stage(job, "WritingAssets", 0.92)
        try:
            linkedin_carousel_path.write_text(linkedin_carousel.strip() + "\n", encoding="utf-8")
            x_thread_path.write_text(x_thread.strip() + "\n", encoding="utf-8")
            newsletter_summary_path.write_text(newsletter_summary.strip() + "\n", encoding="utf-8")
        except Exception as exc:
            raise PipelineError("IO_WRITE_FAILED", "Failed to write generated draft artifacts.", {"message": str(exc)}) from exc

        summary_text = self._build_summary_text(job, hooks_data)
        try:
            summary_path.write_text(summary_text.strip() + "\n", encoding="utf-8")
        except Exception as exc:
            raise PipelineError("IO_WRITE_FAILED", "Failed to write summary.txt.", {"message": str(exc)}) from exc

        if not job.keep_audio:
            try:
                shutil.rmtree(work_dir, ignore_errors=True)
            except Exception:
                pass

        self._write_metadata(
            metadata_path,
            job,
            extra={
                "summaryPath": str(summary_path),
                "hooksPath": str(hooks_path),
                "factsSheetPath": str(facts_sheet_path),
                "linkedinCarouselPath": str(linkedin_carousel_path),
                "xThreadPath": str(x_thread_path),
                "newsletterSummaryPath": str(newsletter_summary_path),
                "draftTone": job.draft_tone,
            },
        )
        self._mark_done(job, summary_text)

    def _resolve_video_metadata(self, job: YouTubeJob, yt_dlp_command: list[str]) -> Dict[str, Any]:
        stdout, _stderr = self._run_command(
            job,
            yt_dlp_command + ["--dump-single-json", "--no-warnings", "--no-playlist", job.video_url],
            failure_code="INVALID_URL",
            failure_message="Unable to resolve YouTube video metadata.",
            timeout_sec=min(self._download_timeout_sec, 300),
        )
        try:
            return json.loads(stdout)
        except Exception as exc:
            raise PipelineError(
                "INVALID_URL",
                "yt-dlp metadata output was not valid JSON.",
                {"message": str(exc)},
            ) from exc

    def _run_command(
        self,
        job: YouTubeJob,
        args: list[str],
        failure_code: str,
        failure_message: str,
        timeout_sec: int,
    ) -> tuple[str, str]:
        started = time.monotonic()
        process = subprocess.Popen(
            args,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        self._attach_process(job, process)
        try:
            while True:
                if job.cancel_event.is_set():
                    self._terminate_process(process)
                    raise PipelineError("JOB_CANCELLED", "Job cancelled by user.")

                if time.monotonic() - started > timeout_sec:
                    self._terminate_process(process)
                    raise PipelineError(
                        failure_code,
                        f"{failure_message} Timeout after {timeout_sec}s.",
                        {"timeoutSec": timeout_sec, "command": args},
                    )

                try:
                    # Repeated communicate(timeout=...) drains pipes incrementally.
                    # This avoids pipe buffer deadlocks on verbose commands
                    # (e.g., yt-dlp --dump-single-json metadata output).
                    stdout, stderr = process.communicate(timeout=0.2)
                    break
                except subprocess.TimeoutExpired:
                    continue
        finally:
            self._detach_process(job)

        if process.returncode != 0:
            stdout_safe, stdout_truncated = _truncate_text(stdout or "", self._stdout_stderr_max_chars)
            stderr_safe, stderr_truncated = _truncate_text(stderr or "", self._stdout_stderr_max_chars)
            raise PipelineError(
                failure_code,
                failure_message,
                {
                    "exitCode": process.returncode,
                    "command": args,
                    "stdout": stdout_safe,
                    "stderr": stderr_safe,
                    "outputTruncated": bool(stdout_truncated or stderr_truncated),
                },
            )

        return stdout or "", stderr or ""

    def _call_llm(
        self,
        job: YouTubeJob,
        system_prompt: str,
        user_prompt: str,
        max_tokens: int,
        temperature: float,
    ) -> str:
        if job.cancel_event.is_set():
            raise PipelineError("JOB_CANCELLED", "Job cancelled by user.")

        endpoint = self._resolve_generation_endpoint(job.summary_config.base_url)
        payload = {
            "model": job.summary_config.model,
            "temperature": float(max(0.0, min(1.0, temperature))),
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "max_tokens": max(64, int(max_tokens)),
        }
        raw = json.dumps(payload).encode("utf-8")
        req = Request(
            endpoint,
            data=raw,
            headers={"Content-Type": "application/json"},
            method="POST",
        )

        try:
            with urlopen(req, timeout=max(10, int(job.summary_config.timeout_sec))) as resp:
                body = resp.read().decode("utf-8", errors="replace")
        except HTTPError as exc:
            body = ""
            try:
                body = exc.read().decode("utf-8", errors="replace")
            except Exception:
                pass
            body_safe, _ = _truncate_text(body, self._stdout_stderr_max_chars)
            raise PipelineError(
                "LLM_REQUEST_FAILED",
                f"Generation engine returned HTTP {exc.code}.",
                {"statusCode": exc.code, "responseBody": body_safe},
            ) from exc
        except URLError as exc:
            raise PipelineError(
                "LLM_REQUEST_FAILED",
                "Generation engine is unavailable.",
                {"message": str(exc.reason)},
            ) from exc
        except Exception as exc:
            raise PipelineError("LLM_REQUEST_FAILED", "Failed to call generation engine.", {"message": str(exc)}) from exc

        try:
            parsed = json.loads(body)
            content = (
                (((parsed.get("choices") or [{}])[0].get("message") or {}).get("content") or "").strip()
            )
        except Exception as exc:
            body_safe, _ = _truncate_text(body, self._stdout_stderr_max_chars)
            raise PipelineError(
                "LLM_REQUEST_FAILED",
                "Generation response could not be parsed.",
                {"message": str(exc), "responseBody": body_safe},
            ) from exc

        if not content:
            raise PipelineError("LLM_REQUEST_FAILED", "Generation engine returned empty content.")
        return content

    def _extract_hooks(self, job: YouTubeJob, transcript_text: str) -> Dict[str, Any]:
        if job.cancel_event.is_set():
            raise PipelineError("JOB_CANCELLED", "Job cancelled by user.")

        transcript = (transcript_text or "").strip()
        if not transcript:
            return {
                "hasTimestamps": False,
                "generatedAtUtc": utc_now(),
                "draftTone": job.draft_tone,
                "hooks": self._build_fallback_hooks(job),
            }

        max_chars = max(2000, int(job.summary_config.max_input_chars))
        transcript_excerpt = self._build_smart_excerpt(transcript, max_chars)
        schema = {
            "hasTimestamps": False,
            "hooks": [
                {
                    "rank": 1,
                    "hook": "string",
                    "who": "string",
                    "outcome": "string",
                    "proof": "string",
                    "supporting_moments": [
                        {
                            "quote": "string",
                            "startSec": None,
                            "endSec": None,
                        }
                    ],
                }
            ],
        }
        schema_json = json.dumps(schema, indent=2)
        system_prompt = (
            "You are a content strategist. Given a video transcript, extract exactly 3 value hooks. "
            "Return ONLY valid JSON matching the provided schema. No markdown fences. No commentary."
        )
        user_prompt = (
            "Return exactly one JSON object that matches this schema:\n"
            f"{schema_json}\n\n"
            f"Video title: {job.title or 'Unknown'}\n"
            f"Channel: {job.channel or 'Unknown'}\n"
            f"DurationSec: {max(0, int(job.duration_sec))}\n"
            f"Draft tone: {job.draft_tone}\n\n"
            "Transcript excerpt:\n"
            f"{transcript_excerpt}"
        )

        first_raw = ""
        try:
            first_raw = self._call_llm(
                job=job,
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                max_tokens=2000,
                temperature=0.2,
            )
            parsed = self._try_parse_json_object(first_raw)
            if parsed is None:
                repaired_raw = self._call_llm(
                    job=job,
                    system_prompt=(
                        "Return ONLY valid JSON. Fix the JSON below to match the schema exactly. "
                        "Do not add text outside the JSON object."
                    ),
                    user_prompt=(
                        "Schema:\n"
                        f"{schema_json}\n\n"
                        "Malformed JSON:\n"
                        f"{first_raw}"
                    ),
                    max_tokens=2200,
                    temperature=0.0,
                )
                parsed = self._try_parse_json_object(repaired_raw)
                if parsed is None:
                    repaired_safe, _ = _truncate_text(repaired_raw, self._stdout_stderr_max_chars)
                    raise PipelineError(
                        "HOOKS_EXTRACTION_FAILED",
                        "Failed to parse hooks JSON after repair retry.",
                        {
                            "subcode": "HOOKS_JSON_INVALID",
                            "responseBody": repaired_safe,
                        },
                    )

            normalized = self._normalize_hooks_payload(parsed)
            if self._hooks_are_placeholder(normalized):
                derived_hooks = self._build_transcript_derived_hooks(job, transcript)
                if derived_hooks:
                    normalized = {"hasTimestamps": False, "hooks": derived_hooks}
            normalized["hasTimestamps"] = False
            normalized["generatedAtUtc"] = utc_now()
            normalized["draftTone"] = job.draft_tone
            return normalized
        except PipelineError as exc:
            if exc.code in {"JOB_CANCELLED", "HOOKS_EXTRACTION_FAILED"}:
                raise
            first_safe, _ = _truncate_text(first_raw, self._stdout_stderr_max_chars)
            raise PipelineError(
                "HOOKS_EXTRACTION_FAILED",
                "Failed to extract value hooks.",
                {
                    "subcode": "HOOKS_JSON_INVALID" if exc.code == "LLM_REQUEST_FAILED" else exc.code,
                    "message": exc.message,
                    "details": exc.details,
                    "responseBody": first_safe,
                },
            ) from exc
        except Exception as exc:
            raise PipelineError(
                "HOOKS_EXTRACTION_FAILED",
                "Failed to extract value hooks.",
                {"subcode": "HOOKS_JSON_INVALID", "message": str(exc)},
            ) from exc

    def _generate_drafts(self, job: YouTubeJob, hooks_data: Dict[str, Any]) -> Tuple[str, str, str]:
        if job.cancel_event.is_set():
            raise PipelineError("JOB_CANCELLED", "Job cancelled by user.")

        hooks_json = json.dumps(hooks_data, indent=2, ensure_ascii=False)
        quote_cues = self._extract_quote_cues(hooks_data, max_quotes=9)
        grounding_context = self._compose_grounding_context(job, hooks_json, quote_cues)
        system_prompt = (
            "You are a professional content writer. Generate social media drafts from value hooks. "
            "Output exactly three sections with exact delimiters and no extra sections. "
            "Ground claims in the provided hook evidence."
        )
        user_prompt = (
            "Use this exact output format:\n"
            "===LINKEDIN_CAROUSEL===\n"
            "Slide 1: ...\n"
            "...\n"
            "===X_THREAD===\n"
            "[1/5] ...\n"
            "[2/5] ...\n"
            "[3/5] ...\n"
            "[4/5] ...\n"
            "[5/5] ...\n"
            "===NEWSLETTER_SUMMARY===\n"
            "...\n\n"
            "Rules:\n"
            "- LinkedIn carousel: 5-8 slides, practical and concise.\n"
            "- X thread: exactly 5 posts, each <= 280 characters.\n"
            "- Newsletter summary: one polished markdown section suitable for monthly newsletter draft.\n"
            "- Keep tone consistent with draft tone.\n\n"
            "- Every substantive claim must be grounded in provided hooks/quotes.\n"
            "- Do NOT invent external facts, numbers, or claims not supported by provided evidence.\n"
            "- If uncertain, phrase cautiously as a draft suggestion.\n\n"
            f"{grounding_context}"
        )

        first_raw = ""
        try:
            first_raw = self._call_llm(
                job=job,
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                max_tokens=3000,
                temperature=0.3,
            )
            sections = self._split_draft_sections(first_raw)
            if sections is None:
                repaired_raw = self._call_llm(
                    job=job,
                    system_prompt=(
                        "You produced malformed sections. Output exactly three sections with exact delimiters. "
                        "Do not include commentary."
                    ),
                    user_prompt=(
                        "Required delimiters:\n"
                        "===LINKEDIN_CAROUSEL===\n"
                        "===X_THREAD===\n"
                        "===NEWSLETTER_SUMMARY===\n\n"
                        "Previous output:\n"
                        f"{first_raw}"
                    ),
                    max_tokens=3200,
                    temperature=0.1,
                )
                sections = self._split_draft_sections(repaired_raw)
                if sections is None:
                    self._logger.warning(
                        "YOUTUBE_DRAFTS_FALLBACK_SPLIT jobId=%s reason=missing_sections_after_repair",
                        job.job_id,
                    )
                    return self._generate_drafts_separately(job, hooks_data, quote_cues)

            linkedin_carousel, x_thread, newsletter_summary = sections
            linkedin_carousel = self._validate_linkedin_carousel(job, linkedin_carousel, hooks_data)
            x_thread = self._validate_x_thread(job, x_thread)
            newsletter_summary = self._validate_newsletter_summary(job, newsletter_summary, hooks_data)

            return linkedin_carousel.strip(), x_thread.strip(), newsletter_summary
        except PipelineError as exc:
            if exc.code in {"JOB_CANCELLED", "DRAFTS_GENERATION_FAILED"}:
                raise
            first_safe, _ = _truncate_text(first_raw, self._stdout_stderr_max_chars)
            raise PipelineError(
                "DRAFTS_GENERATION_FAILED",
                "Failed to generate draft assets.",
                {
                    "subcode": "DRAFTS_VALIDATION_FAILED" if exc.code == "LLM_REQUEST_FAILED" else exc.code,
                    "message": exc.message,
                    "details": exc.details,
                    "responseBody": first_safe,
                },
            ) from exc
        except Exception as exc:
            raise PipelineError(
                "DRAFTS_GENERATION_FAILED",
                "Failed to generate draft assets.",
                {"subcode": "DRAFTS_VALIDATION_FAILED", "message": str(exc)},
            ) from exc

    def _generate_drafts_separately(
        self,
        job: YouTubeJob,
        hooks_data: Dict[str, Any],
        quote_cues: List[str],
    ) -> Tuple[str, str, str]:
        hooks_json = json.dumps(hooks_data, indent=2, ensure_ascii=False)
        grounding_context = self._compose_grounding_context(job, hooks_json, quote_cues)

        linkedin = self._call_llm(
            job=job,
            system_prompt=(
                "You are a professional content writer. Generate ONLY a LinkedIn carousel draft. "
                "Do not include markdown fences or extra sections."
            ),
            user_prompt=(
                "Output only LinkedIn carousel content as 5-8 slides.\n"
                "Use format: 'Slide N: ...'\n"
                "Do not include section delimiters.\n"
                "Ground every claim in provided hooks and quote cues.\n\n"
                f"{grounding_context}"
            ),
            max_tokens=1400,
            temperature=0.25,
        )

        x_thread = self._call_llm(
            job=job,
            system_prompt=(
                "You are a professional content writer. Generate ONLY an X thread draft."
            ),
            user_prompt=(
                "Output exactly 5 posts.\n"
                "Format each as [N/5] text.\n"
                "Each post must be <= 280 characters.\n"
                "Do not include extra sections.\n"
                "Ground claims in the provided hooks and quote cues.\n\n"
                f"{grounding_context}"
            ),
            max_tokens=1200,
            temperature=0.25,
        )

        newsletter = self._call_llm(
            job=job,
            system_prompt=(
                "You are a professional content writer. Generate ONLY a newsletter summary draft."
            ),
            user_prompt=(
                "Output one polished markdown newsletter summary section.\n"
                "No extra sections or delimiters.\n"
                "Ground claims in the provided hooks and quote cues.\n\n"
                f"{grounding_context}"
            ),
            max_tokens=1400,
            temperature=0.25,
        )

        linkedin_clean = self._validate_linkedin_carousel(job, (linkedin or "").strip(), hooks_data)
        x_thread_clean = self._validate_x_thread(job, (x_thread or "").strip())
        newsletter_clean = self._validate_newsletter_summary(job, (newsletter or "").strip(), hooks_data)

        return linkedin_clean.strip(), x_thread_clean.strip(), newsletter_clean

    @staticmethod
    def _extract_quote_cues(hooks_data: Dict[str, Any], max_quotes: int) -> List[str]:
        hooks = hooks_data.get("hooks") if isinstance(hooks_data, dict) else []
        cues: List[str] = []
        seen: set[str] = set()
        if not isinstance(hooks, list):
            return cues

        for hook in hooks:
            if not isinstance(hook, dict):
                continue
            supporting = hook.get("supporting_moments") or hook.get("supportingMoments") or []
            if not isinstance(supporting, list):
                continue
            for moment in supporting:
                quote = ""
                if isinstance(moment, dict):
                    quote = str(moment.get("quote") or "").strip()
                elif isinstance(moment, str):
                    quote = moment.strip()
                if not quote:
                    continue
                key = quote.lower()
                if key in seen:
                    continue
                seen.add(key)
                cues.append(quote)
                if len(cues) >= max(1, max_quotes):
                    return cues
        return cues

    def _compose_grounding_context(self, job: YouTubeJob, hooks_json: str, quote_cues: List[str]) -> str:
        quote_block = "\n".join(f'- "{quote}"' for quote in quote_cues) if quote_cues else '- "No explicit quote cues available."'
        return (
            f"Video title: {job.title or 'Unknown'}\n"
            f"Channel: {job.channel or 'Unknown'}\n"
            f"Draft tone: {job.draft_tone}\n\n"
            "Value hooks JSON:\n"
            f"{hooks_json}\n\n"
            "Quote cues:\n"
            f"{quote_block}"
        )

    def _validate_x_thread(self, job: YouTubeJob, x_thread_text: str) -> str:
        posts = self._normalize_x_thread_posts(self._extract_x_thread_posts(x_thread_text))
        if self._x_thread_within_limit(posts):
            return "\n\n".join(posts)

        try:
            repaired = self._call_llm(
                job=job,
                system_prompt=(
                    "Rewrite ONLY the X thread so it contains exactly 5 posts and each post is <= 280 characters. "
                    "Preserve meaning and keep [1/5]...[5/5] numbering."
                ),
                user_prompt=(
                    "Rewrite this section only:\n"
                    f"{x_thread_text}"
                ),
                max_tokens=900,
                temperature=0.2,
            )
            repaired_posts = self._normalize_x_thread_posts(self._extract_x_thread_posts(repaired))
            if len(repaired_posts) != 5:
                raise PipelineError(
                    "DRAFTS_GENERATION_FAILED",
                    "X thread did not contain exactly 5 posts after repair.",
                    {
                        "subcode": "DRAFTS_VALIDATION_FAILED",
                        "postCount": len(repaired_posts),
                    },
                )
            if self._x_thread_within_limit(repaired_posts):
                return "\n\n".join(repaired_posts)
            truncated = self._truncate_x_thread_posts(repaired_posts)
            if self._x_thread_within_limit(truncated):
                self._logger.warning(
                    "YOUTUBE_X_THREAD_TRUNCATED jobId=%s reason=char_limit_after_repair",
                    job.job_id,
                )
                return "\n\n".join(truncated)
        except PipelineError as exc:
            if exc.code == "JOB_CANCELLED":
                raise
            self._logger.warning(
                "YOUTUBE_X_THREAD_REPAIR_FAILED jobId=%s code=%s message=%s",
                job.job_id,
                exc.code,
                exc.message,
            )

        if len(posts) == 5:
            truncated = self._truncate_x_thread_posts(posts)
            if self._x_thread_within_limit(truncated):
                self._logger.warning(
                    "YOUTUBE_X_THREAD_TRUNCATED jobId=%s reason=char_limit",
                    job.job_id,
                )
                return "\n\n".join(truncated)

        raise PipelineError(
            "DRAFTS_GENERATION_FAILED",
            "Unable to produce a valid 5-post X thread.",
            {"subcode": "DRAFTS_VALIDATION_FAILED", "postCount": len(posts)},
        )

    def _validate_linkedin_carousel(
        self,
        job: YouTubeJob,
        linkedin_text: str,
        hooks_data: Dict[str, Any],
    ) -> str:
        slides = self._extract_linkedin_slides(linkedin_text)
        if 5 <= len(slides) <= 8:
            return "\n\n".join(slides)

        try:
            repaired = self._call_llm(
                job=job,
                system_prompt=(
                    "Rewrite ONLY a LinkedIn carousel. Output exactly 5 to 8 slides."
                ),
                user_prompt=(
                    "Format each slide exactly as 'Slide N: ...'.\n"
                    "Do not include extra sections, delimiters, or explanations.\n"
                    "Preserve the original meaning and keep the requested draft tone.\n\n"
                    f"Draft tone: {job.draft_tone}\n"
                    "Original content:\n"
                    f"{linkedin_text}"
                ),
                max_tokens=1200,
                temperature=0.2,
            )
            repaired_slides = self._extract_linkedin_slides(repaired)
            if 5 <= len(repaired_slides) <= 8:
                return "\n\n".join(repaired_slides)
        except PipelineError as exc:
            if exc.code == "JOB_CANCELLED":
                raise
            self._logger.warning(
                "YOUTUBE_LINKEDIN_REPAIR_FAILED jobId=%s code=%s message=%s",
                job.job_id,
                exc.code,
                exc.message,
            )

        self._logger.warning(
            "YOUTUBE_LINKEDIN_FALLBACK_TEMPLATE jobId=%s reason=slide_count_invalid",
            job.job_id,
        )
        return self._build_fallback_linkedin_carousel(hooks_data)

    def _validate_newsletter_summary(
        self,
        job: YouTubeJob,
        newsletter_text: str,
        hooks_data: Dict[str, Any],
    ) -> str:
        cleaned = (newsletter_text or "").strip()
        if self._newsletter_is_usable(cleaned):
            return cleaned

        try:
            repaired = self._call_llm(
                job=job,
                system_prompt=(
                    "Rewrite ONLY a professional markdown newsletter summary."
                ),
                user_prompt=(
                    "Output markdown only, with this structure:\n"
                    "## Overview\n"
                    "2-3 sentences.\n\n"
                    "### Key Takeaways\n"
                    "- bullet\n"
                    "- bullet\n"
                    "- bullet\n\n"
                    "### Why It Matters\n"
                    "2-3 sentences.\n\n"
                    "Rules: no delimiter markers (===), no placeholder text, no extra sections.\n"
                    f"Draft tone: {job.draft_tone}\n\n"
                    "Original content:\n"
                    f"{cleaned}"
                ),
                max_tokens=1500,
                temperature=0.2,
            )
            repaired_clean = (repaired or "").strip()
            if self._newsletter_is_usable(repaired_clean):
                return repaired_clean
        except PipelineError as exc:
            if exc.code == "JOB_CANCELLED":
                raise
            self._logger.warning(
                "YOUTUBE_NEWSLETTER_REPAIR_FAILED jobId=%s code=%s message=%s",
                job.job_id,
                exc.code,
                exc.message,
            )

        self._logger.warning(
            "YOUTUBE_NEWSLETTER_FALLBACK_TEMPLATE jobId=%s reason=quality_invalid",
            job.job_id,
        )
        return self._build_fallback_newsletter_summary(job, hooks_data)

    @staticmethod
    def _extract_linkedin_slides(raw_text: str) -> List[str]:
        text = (raw_text or "").strip()
        if not text:
            return []

        pattern = re.compile(r"(?im)^\s*slide\s+(\d+)\s*:")
        matches = list(pattern.finditer(text))
        if matches:
            slides: List[str] = []
            for idx, match in enumerate(matches):
                start = match.start()
                end = matches[idx + 1].start() if idx + 1 < len(matches) else len(text)
                block = text[start:end].strip()
                normalized = re.sub(
                    r"(?im)^\s*slide\s+\d+\s*:",
                    f"Slide {idx + 1}:",
                    block,
                    count=1,
                ).strip()
                if normalized:
                    slides.append(normalized)
            return slides

        lines = [line.strip() for line in text.splitlines() if line.strip()]
        if len(lines) >= 5:
            return [f"Slide {idx + 1}: {line}" for idx, line in enumerate(lines[:8])]
        return []

    @staticmethod
    def _newsletter_is_usable(text: str) -> bool:
        if not text:
            return False
        if "===" in text:
            return False
        if len(text) < 320:
            return False
        has_heading = "## " in text or "### " in text
        has_bullets = "\n-" in text or "\n* " in text
        return has_heading and has_bullets

    def _build_fallback_linkedin_carousel(self, hooks_data: Dict[str, Any]) -> str:
        hooks = hooks_data.get("hooks") if isinstance(hooks_data, dict) else []
        normalized_hooks = [hook for hook in hooks if isinstance(hook, dict)][:3]

        slides: List[str] = [
            "Slide 1: Design to Production Is a System\nGreat products are not only designed well; they are designed to be built well."
        ]

        for idx, hook in enumerate(normalized_hooks, start=2):
            hook_title = str(hook.get("hook") or f"Hook {idx - 1}").strip()
            outcome = str(hook.get("outcome") or "").strip()
            proof = str(hook.get("proof") or "").strip()
            body = outcome if outcome else proof
            if proof and outcome and proof.lower() != outcome.lower():
                body = f"{outcome} Proof cue: {proof}"
            slides.append(f"Slide {idx}: {hook_title}\n{body}".strip())

        slides.extend(
            [
                "Slide 5: eBOM and mBOM Need Intentional Mapping\nDesign structure and shop-floor structure can differ by necessity.",
                "Slide 6: Process Planning Creates Repeatability\nDefine operation order, resources, and handoffs so quality scales beyond tribal knowledge.",
                "Slide 7: Next Step\nAudit one active product and map eBOM -> mBOM -> process plan to remove hidden assumptions.",
            ]
        )
        return "\n\n".join(slides[:7])

    def _build_fallback_newsletter_summary(self, job: YouTubeJob, hooks_data: Dict[str, Any]) -> str:
        hooks = hooks_data.get("hooks") if isinstance(hooks_data, dict) else []
        normalized_hooks = [hook for hook in hooks if isinstance(hook, dict)][:3]

        bullet_lines: List[str] = []
        for hook in normalized_hooks:
            hook_text = str(hook.get("hook") or "").strip()
            outcome = str(hook.get("outcome") or "").strip()
            line = outcome or hook_text
            if not line:
                continue
            bullet_lines.append(f"- {line}")
        while len(bullet_lines) < 3:
            bullet_lines.append("- Align design decisions with manufacturing execution constraints early.")

        overview = (
            f"## Overview\n"
            f"This session on {job.title or 'the video topic'} emphasizes a practical shift: "
            "teams should treat product design and manufacturing planning as one connected workflow. "
            "The core message is that CAD definition alone does not guarantee smooth execution on the floor."
        )
        takeaways = "### Key Takeaways\n" + "\n".join(bullet_lines[:4])
        why = (
            "### Why It Matters\n"
            "When eBOM, mBOM, and process planning are aligned earlier, teams reduce rework and improve handoff quality. "
            "That alignment turns digital intent into predictable physical outcomes with fewer surprises."
        )
        return f"{overview}\n\n{takeaways}\n\n{why}"

    @staticmethod
    def _build_smart_excerpt(transcript_text: str, max_chars: int) -> str:
        transcript = (transcript_text or "").strip()
        if max_chars <= 0 or len(transcript) <= max_chars:
            return transcript

        separator = "\n[...]\n"
        sep_total = len(separator) * 2
        if max_chars <= sep_total + 3:
            truncated, _ = _truncate_text(transcript, max_chars)
            return truncated

        # Prefer the explicit shape: first 2k + middle 2k + last 2k.
        # If max_chars is tighter, shrink each slice deterministically.
        target_slice_size = 2000
        available = max_chars - sep_total
        slice_size = min(target_slice_size, available // 3)
        if slice_size < 1:
            truncated, _ = _truncate_text(transcript, max_chars)
            return truncated

        head = transcript[:slice_size]
        mid_start = max(0, (len(transcript) // 2) - (slice_size // 2))
        middle = transcript[mid_start : mid_start + slice_size]
        tail = transcript[-slice_size:]
        return f"{head}{separator}{middle}{separator}{tail}"

    @staticmethod
    def _resolve_generation_endpoint(base_url: str) -> str:
        clean = (base_url or "").strip().rstrip("/")
        if not clean:
            clean = "http://127.0.0.1:1234"
        if clean.endswith("/chat/completions"):
            return clean
        if clean.endswith("/v1"):
            return clean + "/chat/completions"
        return clean + "/v1/chat/completions"

    def _build_summary_text(self, job: YouTubeJob, hooks_data: Dict[str, Any]) -> str:
        hooks = hooks_data.get("hooks") if isinstance(hooks_data, dict) else []
        outcomes: List[str] = []
        if isinstance(hooks, list):
            for item in hooks[:3]:
                if not isinstance(item, dict):
                    continue
                outcome = str(item.get("outcome") or item.get("hook") or "").strip().rstrip(".")
                if outcome:
                    outcomes.append(outcome)

        title = (job.title or "This video").strip()
        if outcomes:
            summary = f"{title} highlights {outcomes[0]}"
            if len(outcomes) > 1:
                summary += "; " + "; ".join(outcomes[1:])
            summary += "."
        else:
            summary = f"{title} was transcribed successfully, but no strong value hooks were extracted."

        if len(summary) > 800:
            summary = summary[:797].rstrip() + "..."
        return summary

    def _build_facts_sheet(self, job: YouTubeJob, hooks_data: Dict[str, Any]) -> Dict[str, Any]:
        hooks = hooks_data.get("hooks") if isinstance(hooks_data, dict) else []
        if not isinstance(hooks, list):
            hooks = []

        first_hook = hooks[0] if hooks and isinstance(hooks[0], dict) else {}
        topic_seed = str(first_hook.get("hook") or first_hook.get("outcome") or "").strip()
        topic_sentence = (
            f"{job.title.strip()} -- {topic_seed}."
            if job.title and topic_seed
            else (job.title.strip() if job.title else topic_seed or "Core topic from transcripted video.")
        ).strip()

        audience = str(first_hook.get("who") or "").strip() if isinstance(first_hook, dict) else ""
        if not audience:
            audience = "professionals seeking actionable insights"

        key_points: List[str] = []
        for item in hooks:
            if not isinstance(item, dict):
                continue
            for candidate in (item.get("outcome"), item.get("proof"), item.get("hook")):
                text = str(candidate or "").strip()
                if not text:
                    continue
                if text in key_points:
                    continue
                key_points.append(text if text.endswith(".") else f"{text}.")
                if len(key_points) >= 5:
                    break
            if len(key_points) >= 5:
                break
        while len(key_points) < 5:
            key_points.append("Additional takeaway available in generated hook drafts.")

        notable_terms = self._extract_notable_terms(job, hooks, limit=3)

        return {
            "generatedAtUtc": utc_now(),
            "topic": topic_sentence,
            "targetAudience": audience,
            "keyPoints": key_points[:5],
            "notableTerms": notable_terms,
            "draftTone": job.draft_tone,
        }

    @staticmethod
    def _extract_notable_terms(job: YouTubeJob, hooks: List[Any], limit: int) -> List[str]:
        seed_texts: List[str] = [job.title or "", job.channel or ""]
        for item in hooks:
            if not isinstance(item, dict):
                continue
            seed_texts.extend(
                [
                    str(item.get("hook") or ""),
                    str(item.get("who") or ""),
                    str(item.get("outcome") or ""),
                    str(item.get("proof") or ""),
                ]
            )

        stopwords = {
            "the",
            "and",
            "for",
            "with",
            "that",
            "this",
            "from",
            "into",
            "your",
            "about",
            "video",
            "draft",
            "value",
            "hook",
            "hooks",
            "summary",
        }
        terms: List[str] = []
        seen: set[str] = set()
        for text in seed_texts:
            for token in re.findall(r"[A-Za-z][A-Za-z0-9_-]{2,}", text or ""):
                key = token.lower()
                if key in stopwords or key in seen:
                    continue
                seen.add(key)
                terms.append(token)
                if len(terms) >= max(1, limit):
                    return terms
        return terms or ["insight", "strategy", "execution"]

    def _build_fallback_hooks(self, job: YouTubeJob) -> List[Dict[str, Any]]:
        title = (job.title or "this video").strip()
        return [
            {
                "rank": 1,
                "hook": "Main theme overview",
                "who": "general audience",
                "outcome": f"Understand the central idea from {title}.",
                "proof": "Transcript contained limited usable speech content.",
                "supporting_moments": [{"quote": "No usable speech transcript was produced.", "startSec": None, "endSec": None}],
            },
            {
                "rank": 2,
                "hook": "Practical framing",
                "who": "practitioners",
                "outcome": "Collect actionable framing points for future content.",
                "proof": "Fallback hook generated due to sparse transcript.",
                "supporting_moments": [{"quote": "Fallback hook generated due to sparse transcript.", "startSec": None, "endSec": None}],
            },
            {
                "rank": 3,
                "hook": "Communication angle",
                "who": "content teams",
                "outcome": "Position the topic in concise, audience-facing language.",
                "proof": "Fallback hook generated due to sparse transcript.",
                "supporting_moments": [{"quote": "Fallback hook generated due to sparse transcript.", "startSec": None, "endSec": None}],
            },
        ]

    @staticmethod
    def _try_parse_json_object(raw_text: str) -> Optional[Dict[str, Any]]:
        raw = (raw_text or "").strip()
        if not raw:
            return None

        candidates: List[str] = [raw]

        fence_match = re.search(r"```(?:json)?\s*(.*?)```", raw, flags=re.IGNORECASE | re.DOTALL)
        if fence_match:
            fenced = fence_match.group(1).strip()
            if fenced:
                candidates.append(fenced)

        first_brace = raw.find("{")
        last_brace = raw.rfind("}")
        if first_brace >= 0 and last_brace > first_brace:
            candidates.append(raw[first_brace : last_brace + 1].strip())

        for candidate in candidates:
            try:
                parsed = json.loads(candidate)
            except Exception:
                continue
            if isinstance(parsed, dict):
                return parsed
        return None

    def _normalize_hooks_payload(self, payload: Dict[str, Any]) -> Dict[str, Any]:
        hooks_raw = payload.get("hooks")
        if not isinstance(hooks_raw, list):
            raise ValueError("hooks payload did not contain a hooks array")

        normalized: List[Dict[str, Any]] = []
        for index, item in enumerate(hooks_raw, start=1):
            if len(normalized) >= 3:
                break
            if not isinstance(item, dict):
                continue

            hook = str(item.get("hook") or "").strip()
            who = str(item.get("who") or "").strip()
            outcome = str(item.get("outcome") or "").strip()
            proof = str(item.get("proof") or "").strip()
            if not hook and not outcome and not proof:
                continue

            supporting_raw = item.get("supporting_moments")
            if supporting_raw is None:
                supporting_raw = item.get("supportingMoments")
            supporting: List[Dict[str, Any]] = []
            if isinstance(supporting_raw, list):
                for moment in supporting_raw:
                    if len(supporting) >= 3:
                        break
                    quote = ""
                    if isinstance(moment, dict):
                        quote = str(moment.get("quote") or "").strip()
                    elif isinstance(moment, str):
                        quote = moment.strip()
                    if quote:
                        supporting.append({"quote": quote, "startSec": None, "endSec": None})

            # Ensure at least 2 quote cues per hook for stronger grounding in draft generation.
            fallback_quotes = [
                proof,
                outcome,
                hook,
                "No supporting quote provided.",
            ]
            for candidate in fallback_quotes:
                if len(supporting) >= 2:
                    break
                quote = str(candidate or "").strip()
                if not quote:
                    continue
                if any(str(entry.get("quote") or "").strip().lower() == quote.lower() for entry in supporting):
                    continue
                supporting.append({"quote": quote, "startSec": None, "endSec": None})
            supporting = supporting[:3]

            normalized.append(
                {
                    "rank": index,
                    "hook": hook or f"Value hook {index}",
                    "who": who or "target audience",
                    "outcome": outcome or proof or hook or "Actionable takeaway identified.",
                    "proof": proof or outcome or hook or "No proof snippet returned.",
                    "supporting_moments": supporting,
                }
            )

        while len(normalized) < 3:
            fallback_index = len(normalized) + 1
            normalized.append(
                {
                    "rank": fallback_index,
                    "hook": f"Value hook {fallback_index}",
                    "who": "target audience",
                    "outcome": "Actionable takeaway identified.",
                    "proof": "Generated fallback hook.",
                    "supporting_moments": [{"quote": "Generated fallback hook.", "startSec": None, "endSec": None}],
                }
            )

        for idx, item in enumerate(normalized, start=1):
            item["rank"] = idx

        return {"hasTimestamps": False, "hooks": normalized[:3]}

    @staticmethod
    def _split_draft_sections(raw_text: str) -> Optional[Tuple[str, str, str]]:
        match = re.search(
            r"===LINKEDIN_CAROUSEL===\s*(.*?)\s*===X_THREAD===\s*(.*?)\s*===NEWSLETTER_SUMMARY===\s*(.*)",
            raw_text or "",
            flags=re.IGNORECASE | re.DOTALL,
        )
        if not match:
            return None
        linkedin, x_thread, newsletter = match.groups()
        return linkedin.strip(), x_thread.strip(), newsletter.strip()

    @staticmethod
    def _cap_linkedin_slides(linkedin_text: str) -> str:
        lines = (linkedin_text or "").splitlines()
        if not lines:
            return ""

        capped_lines: List[str] = []
        slide_count = 0
        keep = True
        for line in lines:
            if re.match(r"^\s*slide\s+\d+\s*:", line, flags=re.IGNORECASE):
                slide_count += 1
                keep = slide_count <= 8
            if keep:
                capped_lines.append(line)

        result = "\n".join(capped_lines).strip()
        return result if result else (linkedin_text or "").strip()

    @staticmethod
    def _extract_x_thread_posts(raw_text: str) -> List[str]:
        text = (raw_text or "").strip()
        if not text:
            return []

        marker_pattern = re.compile(r"(?m)^\s*\[(\d)\/5\]\s*")
        matches = list(marker_pattern.finditer(text))
        posts: List[str] = []
        if matches:
            for idx, match in enumerate(matches):
                start = match.start()
                end = matches[idx + 1].start() if idx + 1 < len(matches) else len(text)
                chunk = text[start:end].strip()
                if chunk:
                    posts.append(chunk)
            return posts

        lines = [line.strip() for line in text.splitlines() if line.strip()]
        return [f"[{idx + 1}/5] {line}" for idx, line in enumerate(lines[:5])]

    @staticmethod
    def _normalize_x_thread_posts(posts: List[str]) -> List[str]:
        normalized: List[str] = []
        for idx, source in enumerate(posts):
            source = source.strip()
            if not source:
                continue
            if not re.match(r"^\s*\[\d\/5\]\s*", source):
                source = f"[{idx + 1}/5] {source}"
            else:
                source = re.sub(r"^\s*\[\d\/5\]\s*", f"[{idx + 1}/5] ", source, count=1)
            normalized.append(source.strip())
        return normalized

    @staticmethod
    def _x_thread_within_limit(posts: List[str]) -> bool:
        return len(posts) == 5 and all(len(post) <= 280 for post in posts)

    @staticmethod
    def _truncate_x_thread_posts(posts: List[str]) -> List[str]:
        truncated: List[str] = []
        for post in posts:
            if len(post) <= 280:
                truncated.append(post)
                continue
            trimmed = post[:277].rstrip() + "..."
            truncated.append(trimmed)
        return truncated

    @staticmethod
    def _hooks_are_placeholder(hooks_payload: Dict[str, Any]) -> bool:
        hooks = hooks_payload.get("hooks") if isinstance(hooks_payload, dict) else []
        if not isinstance(hooks, list) or len(hooks) < 3:
            return True
        placeholder_count = 0
        for item in hooks[:3]:
            if not isinstance(item, dict):
                placeholder_count += 1
                continue
            hook = str(item.get("hook") or "").strip().lower()
            proof = str(item.get("proof") or "").strip().lower()
            outcome = str(item.get("outcome") or "").strip().lower()
            if hook.startswith("value hook ") or proof == "generated fallback hook." or outcome == "actionable takeaway identified.":
                placeholder_count += 1
        return placeholder_count >= 2

    def _build_transcript_derived_hooks(self, job: YouTubeJob, transcript_text: str) -> List[Dict[str, Any]]:
        transcript = (transcript_text or "").strip()
        if not transcript:
            return []

        sentences = [
            chunk.strip()
            for chunk in re.split(r"(?<=[.!?])\s+", transcript)
            if len(chunk.strip()) >= 45
        ]
        if not sentences:
            return []

        keywords = [
            "manufacturing",
            "assembly",
            "process",
            "bom",
            "3d experience",
            "design",
            "shop floor",
            "workflow",
        ]
        selected: List[str] = []
        for sentence in sentences:
            lowered = sentence.lower()
            if any(keyword in lowered for keyword in keywords):
                selected.append(sentence)
            if len(selected) >= 6:
                break
        if len(selected) < 6:
            for sentence in sentences:
                if sentence in selected:
                    continue
                selected.append(sentence)
                if len(selected) >= 6:
                    break
        if len(selected) < 3:
            return []

        hooks: List[Dict[str, Any]] = []
        templates = [
            (
                "Design intent must connect to production intent.",
                "design and manufacturing teams",
                "Align CAD decisions with manufacturing flow earlier in the lifecycle.",
            ),
            (
                "eBOM and mBOM serve different truths and should be modeled accordingly.",
                "PLM owners and manufacturing engineers",
                "Build mBOM around real execution sequence, not only CAD hierarchy.",
            ),
            (
                "Process planning is the bridge from digital model to repeatable output.",
                "operations and process planners",
                "Define operations and resources explicitly to reduce downstream ambiguity.",
            ),
        ]

        for idx in range(3):
            quote_a = selected[idx * 2] if idx * 2 < len(selected) else selected[idx]
            quote_b = selected[idx * 2 + 1] if (idx * 2 + 1) < len(selected) else selected[idx]
            hook_text, who, outcome = templates[idx]
            hooks.append(
                {
                    "rank": idx + 1,
                    "hook": hook_text,
                    "who": who,
                    "outcome": outcome,
                    "proof": quote_a,
                    "supporting_moments": [
                        {"quote": quote_a, "startSec": None, "endSec": None},
                        {"quote": quote_b, "startSec": None, "endSec": None},
                    ],
                }
            )
        return hooks

    def _write_metadata(self, metadata_path: Path, job: YouTubeJob, extra: Dict[str, Any]) -> None:
        payload: Dict[str, Any] = {
            "url": job.video_url,
            "videoId": job.video_id,
            "title": job.title,
            "channel": job.channel,
            "durationSec": job.duration_sec,
            "createdAtUtc": job.created_at_utc,
            "asrProvider": job.asr_engine,
            "asrModel": job.asr_model,
            "transcriptPath": job.transcript_path,
            "outputDir": job.output_dir,
        }
        payload.update(extra)
        metadata_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    def _get_job_internal(self, job_id: str) -> Optional[YouTubeJob]:
        with self._lock:
            return self._jobs.get(job_id)

    def _set_stage(self, job: YouTubeJob, stage: str, progress: float, status: str = "Running") -> None:
        with self._lock:
            if self._is_terminal(job):
                return
            job.stage = stage
            job.status = status
            job.progress = max(0.0, min(1.0, progress))
            job.updated_ts = time.time()
            job.updated_at_utc = utc_now()
            self._logger.info(
                "YOUTUBE_JOB_STAGE_CHANGED jobId=%s status=%s stage=%s progress=%.3f",
                job.job_id,
                job.status,
                job.stage,
                job.progress,
            )

    def _mark_done(self, job: YouTubeJob, summary: str) -> None:
        with self._lock:
            if self._is_terminal(job):
                return
            job.status = "Done"
            job.stage = "Done"
            job.progress = 1.0
            job.summary = summary
            job.error = None
            job.updated_ts = time.time()
            job.updated_at_utc = utc_now()
            self._logger.info("YOUTUBE_JOB_DONE jobId=%s outputDir=%s", job.job_id, job.output_dir)

    def _mark_cancelled(self, job: YouTubeJob, message: str) -> None:
        with self._lock:
            self._mark_cancelled_locked(job, message)

    def _mark_cancelled_locked(self, job: YouTubeJob, message: str) -> None:
        if self._is_terminal(job):
            return
        job.status = "Cancelled"
        job.stage = "Cancelled"
        job.error = {
            "code": "JOB_CANCELLED",
            "message": message,
            "details": {},
        }
        job.progress = max(0.0, min(1.0, job.progress))
        job.updated_ts = time.time()
        job.updated_at_utc = utc_now()
        self._logger.info("YOUTUBE_JOB_CANCELLED jobId=%s message=%s", job.job_id, message)

    def _mark_failed(self, job: YouTubeJob, code: str, message: str, details: Optional[Dict[str, Any]] = None) -> None:
        with self._lock:
            if self._is_terminal(job):
                return
            job.status = "Failed"
            job.stage = "Failed"
            job.error = {"code": code, "message": message, "details": details or {}}
            job.updated_ts = time.time()
            job.updated_at_utc = utc_now()
            self._logger.error(
                "YOUTUBE_JOB_FAILED jobId=%s code=%s message=%s",
                job.job_id,
                code,
                message,
            )

    def _attach_process(self, job: YouTubeJob, process: subprocess.Popen[str]) -> None:
        with self._lock:
            job.active_process = process

    def _detach_process(self, job: YouTubeJob) -> None:
        with self._lock:
            job.active_process = None

    def _kill_active_process_locked(self, job: YouTubeJob) -> None:
        process = job.active_process
        if process is None:
            return
        self._terminate_process(process)
        job.active_process = None

    @staticmethod
    def _terminate_process(process: subprocess.Popen[str]) -> None:
        try:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=5)
        except Exception:
            try:
                process.kill()
            except Exception:
                pass

    def _to_response(self, job: YouTubeJob) -> Dict[str, Any]:
        return {
            "jobId": job.job_id,
            "status": job.status,
            "stage": job.stage,
            "progress": round(job.progress, 4),
            "video": {
                "videoId": job.video_id,
                "title": job.title,
                "channel": job.channel,
                "durationSec": job.duration_sec,
            },
            "outputDir": job.output_dir,
            "transcriptPath": job.transcript_path,
            "summaryPath": job.summary_path,
            "hooksPath": job.hooks_path,
            "factsSheetPath": job.facts_sheet_path,
            "linkedinCarouselPath": job.linkedin_carousel_path,
            "xThreadPath": job.x_thread_path,
            "newsletterSummaryPath": job.newsletter_summary_path,
            "summary": job.summary,
            "error": job.error,
            "createdAtUtc": job.created_at_utc,
            "updatedAtUtc": job.updated_at_utc,
            "keepAudio": job.keep_audio,
        }

    @staticmethod
    def _is_terminal(job: YouTubeJob) -> bool:
        return job.status in {"Done", "Failed", "Cancelled"}

    def _evict_locked(self) -> None:
        now = time.time()
        stale_ids = [
            job_id
            for job_id in list(self._order)
            if (job := self._jobs.get(job_id)) is not None
            and self._is_terminal(job)
            and (now - job.updated_ts) > self._ttl_seconds
        ]
        for job_id in stale_ids:
            self._jobs.pop(job_id, None)
            try:
                self._order.remove(job_id)
            except ValueError:
                pass

        while len(self._order) > self._max_history:
            oldest = self._order[0]
            old_job = self._jobs.get(oldest)
            if old_job is not None and not self._is_terminal(old_job):
                # Avoid evicting active jobs.
                break
            self._order.popleft()
            self._jobs.pop(oldest, None)
