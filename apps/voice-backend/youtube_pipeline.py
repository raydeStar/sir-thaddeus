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
from typing import Any, Callable, Dict, Optional
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
    status: str = "Queued"
    stage: str = "Resolving"
    progress: float = 0.0
    created_at_utc: str = field(default_factory=utc_now)
    updated_at_utc: str = field(default_factory=utc_now)
    created_ts: float = field(default_factory=time.time, repr=False)
    updated_ts: float = field(default_factory=time.time, repr=False)
    output_dir: str = ""
    transcript_path: str = ""
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
        metadata_path = output_dir / "metadata.json"
        job.output_dir = str(output_dir)
        job.transcript_path = str(transcript_path)

        self._write_metadata(metadata_path, job, extra={})

        self._set_stage(job, "DownloadingAudio", 0.20)
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

        self._set_stage(job, "ConvertingAudio", 0.38)
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

        self._set_stage(job, "Transcribing", 0.62)
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

        self._set_stage(job, "WritingTranscript", 0.84)
        try:
            transcript_path.write_text(transcript_text + ("\n" if transcript_text else ""), encoding="utf-8")
        except Exception as exc:
            raise PipelineError("IO_WRITE_FAILED", "Failed to write transcript.txt.", {"message": str(exc)}) from exc

        self._set_stage(job, "Summarizing", 0.92)
        summary_text = self._summarize(job, transcript_text)
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
            },
        )
        self._mark_done(job, summary_text)

    def _resolve_video_metadata(self, job: YouTubeJob, yt_dlp_command: list[str]) -> Dict[str, Any]:
        self._set_stage(job, "Resolving", 0.08)
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

    def _summarize(self, job: YouTubeJob, transcript_text: str) -> str:
        if job.cancel_event.is_set():
            raise PipelineError("JOB_CANCELLED", "Job cancelled by user.")

        if not transcript_text.strip():
            return "- Key points:\n  - No spoken content detected.\n\nWhat this is about:\nNo usable speech transcript was produced."

        max_chars = max(2000, int(job.summary_config.max_input_chars))
        transcript_input, was_truncated = _truncate_text(transcript_text.strip(), max_chars)

        summary_endpoint = self._resolve_summary_endpoint(job.summary_config.base_url)
        user_prompt = (
            "Summarize this transcript for UI display.\n"
            "Output format:\n"
            "1) 'What this is about' section with 1-2 lines.\n"
            "2) 'Key points' section with 5-10 bullet points.\n"
            "3) Optional 'Action items / decisions' section only if clearly present.\n"
            "Be concise and factual.\n\n"
            f"Transcript (truncated={str(was_truncated).lower()}):\n{transcript_input}"
        )
        payload = {
            "model": job.summary_config.model,
            "temperature": float(job.summary_config.temperature),
            "messages": [
                {
                    "role": "system",
                    "content": "You produce concise, structured transcript summaries for desktop UIs.",
                },
                {"role": "user", "content": user_prompt},
            ],
            "max_tokens": 700,
        }
        raw = json.dumps(payload).encode("utf-8")
        req = Request(
            summary_endpoint,
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
                "SUMMARY_FAILED",
                f"Summary engine returned HTTP {exc.code}.",
                {"statusCode": exc.code, "responseBody": body_safe},
            ) from exc
        except URLError as exc:
            raise PipelineError(
                "SUMMARY_FAILED",
                "Summary engine is unavailable.",
                {"message": str(exc.reason)},
            ) from exc
        except Exception as exc:
            raise PipelineError("SUMMARY_FAILED", "Failed to call summary engine.", {"message": str(exc)}) from exc

        try:
            parsed = json.loads(body)
            summary = (
                (((parsed.get("choices") or [{}])[0].get("message") or {}).get("content") or "").strip()
            )
        except Exception as exc:
            body_safe, _ = _truncate_text(body, self._stdout_stderr_max_chars)
            raise PipelineError(
                "SUMMARY_FAILED",
                "Summary response could not be parsed.",
                {"message": str(exc), "responseBody": body_safe},
            ) from exc

        if not summary:
            raise PipelineError("SUMMARY_FAILED", "Summary engine returned an empty summary.")
        return summary

    @staticmethod
    def _resolve_summary_endpoint(base_url: str) -> str:
        clean = (base_url or "").strip().rstrip("/")
        if not clean:
            clean = "http://127.0.0.1:1234"
        if clean.endswith("/chat/completions"):
            return clean
        if clean.endswith("/v1"):
            return clean + "/chat/completions"
        return clean + "/v1/chat/completions"

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
