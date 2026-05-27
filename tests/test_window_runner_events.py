"""Tests that ``window_runner.run_window`` emits dashboard events."""

from __future__ import annotations

import datetime as dt
from collections.abc import Iterable

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.agents.canned_llm import CannedCouncilLLM
from stockripper.agents.registry import build_registry
from stockripper.agents.schemas import EvidencePacket
from stockripper.agents.window_runner import run_window
from stockripper.dashboard.events import DashboardEvent, EventName
from stockripper.db import Base, build_engine
from stockripper.tracks import seed_default_tracks

_FROZEN_NOW = dt.datetime(2026, 5, 28, 14, 30, 0, tzinfo=dt.UTC)


@pytest.fixture
def session_factory() -> sessionmaker[Session]:
    engine = build_engine("sqlite:///:memory:")
    Base.metadata.create_all(bind=engine)
    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    s = factory()
    seed_default_tracks(s)
    s.commit()
    s.close()
    return factory


def _llm_factory(packet: EvidencePacket) -> CannedCouncilLLM:
    return CannedCouncilLLM(packet=packet, clock=_FROZEN_NOW)


class _CaptureEmitter:
    """Test emitter that captures every published event in order."""

    def __init__(self) -> None:
        self.events: list[DashboardEvent] = []

    async def publish(self, event: DashboardEvent) -> None:
        self.events.append(event)


def _has(events: Iterable[DashboardEvent], name: EventName) -> bool:
    return any(e.event == name for e in events)


async def test_run_window_emits_lifecycle_events(
    session_factory: sessionmaker[Session],
) -> None:
    emitter = _CaptureEmitter()
    registry = build_registry()
    await run_window(
        registry=registry,
        track_ids=("balanced",),
        symbols=("AAPL",),
        window_label="opening",
        config_hash="cfg-events",
        now=_FROZEN_NOW,
        llm_factory=_llm_factory,
        session_factory=session_factory,
        event_emitter=emitter,
    )

    assert _has(emitter.events, EventName.WINDOW_STARTED)
    assert _has(emitter.events, EventName.TRACK_STARTED)
    assert _has(emitter.events, EventName.AGENT_COMPLETED)
    assert _has(emitter.events, EventName.JUDGE_DECIDED)
    assert _has(emitter.events, EventName.TRACK_COMPLETED)
    assert _has(emitter.events, EventName.WINDOW_COMPLETED)


async def test_emitter_failure_does_not_break_window(
    session_factory: sessionmaker[Session],
) -> None:
    class _BrokenEmitter:
        async def publish(self, event: DashboardEvent) -> None:
            raise RuntimeError("boom")

    registry = build_registry()
    result = await run_window(
        registry=registry,
        track_ids=("balanced",),
        symbols=("AAPL",),
        window_label="opening",
        config_hash="cfg-broken",
        now=_FROZEN_NOW,
        llm_factory=_llm_factory,
        session_factory=session_factory,
        event_emitter=_BrokenEmitter(),
    )
    assert result.status in {"ok", "partial"}
    assert len(result.outcomes) == 1
