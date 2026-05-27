"""TTL JSON file cache used by data adapters.

Design notes (per Phase 2 rubber-duck critique):

- **Atomic writes.** Always write to ``<key>.tmp`` then ``os.replace`` so a
  partial write never produces a corrupt cache file.
- **Corrupt-file resilience.** A JSON decode error or missing version is
  treated as a cache miss and the file is unlinked; the caller will refetch
  and overwrite.
- **Versioned payloads.** Each on-disk record carries a ``schema_version``
  so future format changes are non-breaking.
- **No concurrency promises across processes.** This is a single-process
  TTL cache. Phase 3 may replace it with a SQL cache table if needed.
"""

from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import re
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Final

_SCHEMA_VERSION: Final[int] = 1
_DEFAULT_CACHE_ROOT: Final[Path] = Path(".data-cache")
_SAFE_KEY_RE = re.compile(r"^[A-Za-z0-9_.-]+$")


def _utcnow() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def _safe_filename(key: str) -> str:
    """Normalise an arbitrary cache key into a safe filename.

    Plain keys pass through; anything containing path separators or other
    unsafe characters is replaced with a sha256 digest so the filesystem
    layout stays flat and predictable.
    """

    if _SAFE_KEY_RE.match(key) and len(key) <= 120:
        return key
    return "sha256_" + hashlib.sha256(key.encode("utf-8")).hexdigest()


@dataclass
class CacheEntry:
    """In-memory view of a cache record."""

    key: str
    value: Any
    expires_at: dt.datetime
    written_at: dt.datetime


class JsonFileCache:
    """Simple TTL JSON cache backed by a directory of files.

    Each provider gets its own subdirectory (``alpaca``, ``sec_edgar`` etc.)
    so on-disk layout is human-skimmable when debugging.
    """

    def __init__(self, root: Path | str | None = None) -> None:
        self._root = Path(root) if root is not None else _DEFAULT_CACHE_ROOT
        self._lock = threading.Lock()

    @property
    def root(self) -> Path:
        return self._root

    def _path(self, namespace: str, key: str) -> Path:
        return self._root / namespace / (_safe_filename(key) + ".json")

    def get(self, namespace: str, key: str) -> CacheEntry | None:
        """Return the entry if present and not expired; otherwise None."""

        path = self._path(namespace, key)
        if not path.exists():
            return None
        try:
            raw = path.read_text(encoding="utf-8")
            record = json.loads(raw)
        except (OSError, json.JSONDecodeError):
            with self._lock:
                path.unlink(missing_ok=True)
            return None
        if record.get("schema_version") != _SCHEMA_VERSION:
            with self._lock:
                path.unlink(missing_ok=True)
            return None
        try:
            expires_at = dt.datetime.fromisoformat(record["expires_at"])
            written_at = dt.datetime.fromisoformat(record["written_at"])
        except (KeyError, ValueError):
            with self._lock:
                path.unlink(missing_ok=True)
            return None
        if expires_at <= _utcnow():
            return None
        return CacheEntry(
            key=key,
            value=record["value"],
            expires_at=expires_at,
            written_at=written_at,
        )

    def put(
        self,
        namespace: str,
        key: str,
        value: Any,
        *,
        ttl: dt.timedelta,
    ) -> CacheEntry:
        """Write a value atomically with the requested TTL."""

        now = _utcnow()
        expires_at = now + ttl
        record = {
            "schema_version": _SCHEMA_VERSION,
            "namespace": namespace,
            "key": key,
            "value": value,
            "written_at": now.isoformat(),
            "expires_at": expires_at.isoformat(),
        }
        path = self._path(namespace, key)
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp_path = path.with_suffix(path.suffix + ".tmp")
        with self._lock:
            tmp_path.write_text(
                json.dumps(record, default=str, separators=(",", ":")),
                encoding="utf-8",
            )
            os.replace(tmp_path, path)
        return CacheEntry(key=key, value=value, expires_at=expires_at, written_at=now)

    def delete(self, namespace: str, key: str) -> None:
        path = self._path(namespace, key)
        with self._lock:
            path.unlink(missing_ok=True)


__all__ = ("CacheEntry", "JsonFileCache")
