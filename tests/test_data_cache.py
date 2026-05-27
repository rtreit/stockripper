"""Tests for the atomic JSON file cache."""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path

from stockripper.data.cache import JsonFileCache


def test_put_then_get_returns_value(tmp_path: Path) -> None:
    cache = JsonFileCache(tmp_path)
    cache.put("alpaca", "bars:AAPL:20d", {"hello": "world"}, ttl=dt.timedelta(minutes=1))
    entry = cache.get("alpaca", "bars:AAPL:20d")
    assert entry is not None
    assert entry.value == {"hello": "world"}


def test_get_after_expiry_returns_none(tmp_path: Path) -> None:
    cache = JsonFileCache(tmp_path)
    cache.put("p", "k", 42, ttl=dt.timedelta(seconds=-5))  # already expired
    assert cache.get("p", "k") is None


def test_corrupt_file_treated_as_miss_and_removed(tmp_path: Path) -> None:
    cache = JsonFileCache(tmp_path)
    path = tmp_path / "p" / "k.json"
    path.parent.mkdir()
    path.write_text("{not valid json", encoding="utf-8")
    assert cache.get("p", "k") is None
    assert not path.exists()


def test_unknown_schema_version_is_invalidated(tmp_path: Path) -> None:
    cache = JsonFileCache(tmp_path)
    path = tmp_path / "p" / "k.json"
    path.parent.mkdir()
    path.write_text(json.dumps({"schema_version": 999, "value": 1}), encoding="utf-8")
    assert cache.get("p", "k") is None
    assert not path.exists()


def test_unsafe_key_is_hashed(tmp_path: Path) -> None:
    cache = JsonFileCache(tmp_path)
    weird = "a/b/c:1?q=2"
    cache.put("p", weird, "ok", ttl=dt.timedelta(minutes=1))
    files = list((tmp_path / "p").iterdir())
    assert len(files) == 1
    assert files[0].name.startswith("sha256_")
    entry = cache.get("p", weird)
    assert entry is not None and entry.value == "ok"


def test_atomic_write_no_tmp_leftovers(tmp_path: Path) -> None:
    cache = JsonFileCache(tmp_path)
    for i in range(5):
        cache.put("p", f"k{i}", i, ttl=dt.timedelta(minutes=1))
    leftovers = [p for p in (tmp_path / "p").iterdir() if p.suffix == ".tmp"]
    assert leftovers == []


def test_delete_removes_entry(tmp_path: Path) -> None:
    cache = JsonFileCache(tmp_path)
    cache.put("p", "k", "v", ttl=dt.timedelta(minutes=1))
    cache.delete("p", "k")
    assert cache.get("p", "k") is None
