"""Tests for symbols_by_track per-track universe wiring in run_window."""

from __future__ import annotations

import asyncio

import pytest

from stockripper.agents.registry import build_registry
from stockripper.agents.window_runner import run_window


@pytest.fixture
def registry() -> object:
    return build_registry()


def test_symbols_by_track_overrides_global_symbols(registry: object) -> None:
    """When symbols_by_track has an entry, that wins over the global list."""

    track_ids = ("aggressive", "conservative")
    by_track: dict[str, tuple[str, ...]] = {
        "aggressive": ("NVDA", "TSLA"),
        "conservative": ("SPY",),
    }

    result = asyncio.run(
        run_window(
            registry=registry,  # type: ignore[arg-type]
            track_ids=track_ids,
            symbols=("IGNORED",),
            symbols_by_track=by_track,
            persist=False,
        )
    )

    pairs = {(o.track_id, o.symbol) for o in result.outcomes}
    assert ("aggressive", "NVDA") in pairs
    assert ("aggressive", "TSLA") in pairs
    assert ("conservative", "SPY") in pairs
    # Global override list never leaks into a track that has its own entry.
    assert not any(symbol == "IGNORED" for _, symbol in pairs)


def test_symbols_fallback_used_when_track_missing_from_map(
    registry: object,
) -> None:
    """Tracks absent from symbols_by_track fall back to the global symbols."""

    track_ids = ("aggressive", "balanced")
    by_track: dict[str, tuple[str, ...]] = {"aggressive": ("NVDA",)}

    result = asyncio.run(
        run_window(
            registry=registry,  # type: ignore[arg-type]
            track_ids=track_ids,
            symbols=("AAPL",),
            symbols_by_track=by_track,
            persist=False,
        )
    )

    pairs = {(o.track_id, o.symbol) for o in result.outcomes}
    assert ("aggressive", "NVDA") in pairs
    assert ("balanced", "AAPL") in pairs


def test_empty_symbols_and_no_per_track_map_yields_no_work(
    registry: object,
) -> None:
    """No symbols anywhere means no outcomes — guard against silent default."""

    result = asyncio.run(
        run_window(
            registry=registry,  # type: ignore[arg-type]
            track_ids=("aggressive",),
            symbols=(),
            symbols_by_track=None,
            persist=False,
        )
    )

    assert result.outcomes == ()
