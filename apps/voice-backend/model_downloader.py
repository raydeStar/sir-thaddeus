"""Declarative model downloader — fetches missing ML artifacts on startup.

Reads `model_registry.json` to resolve download URLs, then streams each
file to disk with SHA-256 verification and human-readable progress.

Design notes:
    - Downloads are bounded (max size enforced per file).
    - Partial downloads are written to a .tmp suffix and renamed atomically
      on success, so interrupted downloads never leave corrupt artifacts.
    - All activity is logged through the standard `logging` module.
    - No implicit networking: callers must explicitly invoke `ensure_models`.
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
import shutil
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple
from urllib.request import Request, urlopen
from urllib.error import URLError, HTTPError

logger = logging.getLogger("model-downloader")

# ─── Safety bounds ────────────────────────────────────────────────────────────
MAX_DOWNLOAD_BYTES = 500 * 1024 * 1024       # 500 MB hard ceiling per file
CHUNK_SIZE         = 256 * 1024               # 256 KB read chunks
CONNECT_TIMEOUT_S  = 30                       # seconds
DEFAULT_VARIANT    = "v1.0"                   # default kokoro variant


# ─── Registry helpers ─────────────────────────────────────────────────────────

def _load_registry(registry_path: Path) -> Dict[str, Any]:
    """Load and return the model registry JSON."""
    if not registry_path.exists():
        raise FileNotFoundError(f"Model registry not found: {registry_path}")
    with open(registry_path, "r", encoding="utf-8") as f:
        return json.load(f)


def resolve_kokoro_files(
    registry_path: Path,
    variant: Optional[str] = None,
) -> List[Dict[str, Any]]:
    """Resolve the file list for a given Kokoro model variant.

    Returns a list of dicts with keys: localName, url, sha256, sizeBytes.
    """
    registry = _load_registry(registry_path)
    kokoro = registry.get("kokoro", {})

    chosen = variant or DEFAULT_VARIANT
    if chosen not in kokoro:
        available = ", ".join(sorted(kokoro.keys()))
        raise KeyError(
            f"Kokoro variant '{chosen}' not in registry. "
            f"Available: {available}"
        )

    return kokoro[chosen].get("files", [])


# ─── Download engine ──────────────────────────────────────────────────────────

def _format_bytes(n: int) -> str:
    """Pretty-print byte count."""
    if n >= 1_073_741_824:
        return f"{n / 1_073_741_824:.1f} GB"
    if n >= 1_048_576:
        return f"{n / 1_048_576:.1f} MB"
    if n >= 1_024:
        return f"{n / 1_024:.1f} KB"
    return f"{n} B"


def _download_file(
    url: str,
    dest: Path,
    expected_size: Optional[int] = None,
    expected_sha256: Optional[str] = None,
) -> Tuple[int, str]:
    """Stream a file from *url* to *dest*, returning (bytes_written, sha256).

    Writes to a temporary file first, then renames on success.
    Raises on network errors, size violations, or hash mismatches.
    """
    tmp_path = dest.with_suffix(dest.suffix + ".tmp")
    dest.parent.mkdir(parents=True, exist_ok=True)

    logger.info("Downloading %s", url)
    logger.info("  -> %s", dest)
    if expected_size:
        logger.info("  Expected size: %s", _format_bytes(expected_size))

    req = Request(url, headers={"User-Agent": "voice-backend-model-downloader/1.0"})

    try:
        resp = urlopen(req, timeout=CONNECT_TIMEOUT_S)
    except HTTPError as exc:
        raise RuntimeError(f"HTTP {exc.code} downloading {url}") from exc
    except URLError as exc:
        raise RuntimeError(f"Network error downloading {url}: {exc.reason}") from exc

    content_length = resp.headers.get("Content-Length")
    total = int(content_length) if content_length else expected_size

    if total and total > MAX_DOWNLOAD_BYTES:
        raise RuntimeError(
            f"File exceeds safety limit: {_format_bytes(total)} > "
            f"{_format_bytes(MAX_DOWNLOAD_BYTES)}"
        )

    sha = hashlib.sha256()
    written = 0
    t0 = time.monotonic()
    last_log = t0

    try:
        with open(tmp_path, "wb") as fout:
            while True:
                chunk = resp.read(CHUNK_SIZE)
                if not chunk:
                    break
                fout.write(chunk)
                sha.update(chunk)
                written += len(chunk)

                if written > MAX_DOWNLOAD_BYTES:
                    raise RuntimeError(
                        f"Download exceeded safety limit at {_format_bytes(written)}"
                    )

                # Progress logging every ~5 seconds
                now = time.monotonic()
                if now - last_log >= 5.0:
                    pct = f" ({written * 100 // total}%)" if total else ""
                    elapsed = now - t0
                    speed = written / elapsed if elapsed > 0 else 0
                    logger.info(
                        "  Progress: %s%s  [%s/s]",
                        _format_bytes(written), pct, _format_bytes(int(speed)),
                    )
                    last_log = now
    except Exception:
        # Clean up partial download on any error
        if tmp_path.exists():
            tmp_path.unlink()
        raise

    elapsed = time.monotonic() - t0
    digest = sha.hexdigest()

    # Verify SHA-256 if expected
    if expected_sha256 and digest != expected_sha256.lower():
        tmp_path.unlink()
        raise RuntimeError(
            f"SHA-256 mismatch for {dest.name}: "
            f"expected {expected_sha256}, got {digest}"
        )

    # Atomic rename: tmp -> final
    if dest.exists():
        dest.unlink()
    shutil.move(str(tmp_path), str(dest))

    speed = written / elapsed if elapsed > 0 else 0
    logger.info(
        "  Complete: %s in %.1fs (%s/s)  sha256=%s",
        _format_bytes(written), elapsed, _format_bytes(int(speed)), digest,
    )

    return written, digest


# ─── Public API ───────────────────────────────────────────────────────────────

def ensure_kokoro_models(
    voices_root: Path,
    voice_id: str,
    registry_path: Path,
    variant: Optional[str] = None,
    *,
    force: bool = False,
) -> bool:
    """Ensure Kokoro model files exist for *voice_id*, downloading if needed.

    Args:
        voices_root:    Path to the voices directory (e.g. apps/voice-backend/voices).
        voice_id:       Target voice (e.g. "af_sky").
        registry_path:  Path to model_registry.json.
        variant:        Kokoro model variant key (default: "v1.0").
        force:          Re-download even if files exist.

    Returns:
        True if any files were downloaded, False if all were already present.
    """
    files = resolve_kokoro_files(registry_path, variant)
    voice_dir = voices_root / voice_id
    downloaded_any = False

    for entry in files:
        local_name = entry["localName"]
        dest = voice_dir / local_name

        if dest.exists() and not force:
            logger.info("Model file present: %s (%s)", dest, _format_bytes(dest.stat().st_size))
            continue

        url = entry.get("url")
        if not url:
            logger.warning("No download URL for %s — skipping.", local_name)
            continue

        _download_file(
            url=url,
            dest=dest,
            expected_size=entry.get("sizeBytes"),
            expected_sha256=entry.get("sha256"),
        )
        downloaded_any = True

    # Regenerate manifest after download
    if downloaded_any:
        _write_manifest(voice_dir, voice_id)

    return downloaded_any


def _write_manifest(voice_dir: Path, voice_id: str) -> None:
    """Write a manifest.json for the voice directory (matches install-kokoro-assets.ps1 format)."""
    files = []
    for path in sorted(voice_dir.rglob("*")):
        if not path.is_file() or path.name == "manifest.json" or path.suffix == ".tmp":
            continue
        sha = hashlib.sha256(path.read_bytes()).hexdigest()
        rel = path.relative_to(voice_dir).as_posix()
        files.append({"path": rel, "sha256": sha})

    manifest = {
        "voiceId": voice_id,
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "autoDownloaded": True,
        "files": files,
    }

    manifest_path = voice_dir / "manifest.json"
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    logger.info("Manifest written: %s (%d files)", manifest_path, len(files))
