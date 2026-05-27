"""Tests for the Phase-4 persistence translator."""

from __future__ import annotations

import datetime as dt

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.agents.canned_llm import CannedCouncilLLM
from stockripper.agents.demo import build_demo_packet
from stockripper.agents.ids import (
    packet_id as derive_packet_id,
)
from stockripper.agents.ids import (
    track_run_id as derive_track_run_id,
)
from stockripper.agents.ids import (
    window_run_id as derive_window_run_id,
)
from stockripper.agents.orchestrator import run_track
from stockripper.agents.persistence import (
    persist_failed_track,
    persist_skipped_track,
    persist_track_run,
)
from stockripper.agents.registry import build_registry
from stockripper.db import Base, Repository, build_engine
from stockripper.db.models import AgentRun, JudgeDecision, TrackRun
from stockripper.tracks import seed_default_tracks

_FROZEN_NOW = dt.datetime(2026, 5, 28, 14, 30, 0, tzinfo=dt.UTC)


@pytest.fixture
def session() -> Session:
    engine = build_engine("sqlite:///:memory:")
    Base.metadata.create_all(bind=engine)
    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    s = factory()
    seed_default_tracks(s)
    s.commit()
    return s


async def test_persist_track_run_writes_track_agent_recs_and_decision(
    session: Session,
) -> None:
    registry = build_registry()
    run_id = derive_window_run_id(
        window_label="opening", trading_day=_FROZEN_NOW.date(),
        config_hash="cfg", started_at=_FROZEN_NOW,
    )
    pid = derive_packet_id(
        track_id="balanced", window_run_id=run_id, symbol="AAPL",
    )
    packet = build_demo_packet(
        symbol="AAPL", track_id="balanced", window_id="opening",
        packet_id=pid, now=_FROZEN_NOW,
    )
    llm = CannedCouncilLLM(packet=packet, clock=_FROZEN_NOW)
    result = await run_track(
        registry=registry,
        track_id="balanced",
        packet=packet,
        llm=llm,
        window_id="opening",
        window_run_id=run_id,
        now=_FROZEN_NOW,
    )

    repo = Repository(session)
    repo.create_run(
        run_id=run_id, window_label="opening", trading_day=_FROZEN_NOW.date(),
        config_hash="cfg", started_at=_FROZEN_NOW,
    )
    persist_track_run(repo, run_id=run_id, result=result, completed_at=_FROZEN_NOW)
    session.commit()

    track_runs = session.query(TrackRun).filter(TrackRun.run_id == run_id).all()
    assert len(track_runs) == 1
    assert track_runs[0].track_run_id == result.track_run_id

    agent_runs = session.query(AgentRun).filter(AgentRun.run_id == run_id).all()
    assert len(agent_runs) >= 1
    # Every persisted agent run carries the deterministic agr_ prefix
    assert all(ar.agent_run_id.startswith("agr_") for ar in agent_runs)

    decisions = session.query(JudgeDecision).filter(
        JudgeDecision.run_id == run_id
    ).all()
    assert len(decisions) == 1


async def test_persist_track_run_is_idempotent(session: Session) -> None:
    registry = build_registry()
    run_id = derive_window_run_id(
        window_label="opening", trading_day=_FROZEN_NOW.date(),
        config_hash="cfg", started_at=_FROZEN_NOW,
    )
    pid = derive_packet_id(
        track_id="balanced", window_run_id=run_id, symbol="AAPL",
    )
    packet = build_demo_packet(
        symbol="AAPL", track_id="balanced", window_id="opening",
        packet_id=pid, now=_FROZEN_NOW,
    )
    llm = CannedCouncilLLM(packet=packet, clock=_FROZEN_NOW)
    result = await run_track(
        registry=registry, track_id="balanced", packet=packet, llm=llm,
        window_id="opening", window_run_id=run_id, now=_FROZEN_NOW,
    )

    repo = Repository(session)
    repo.create_run(
        run_id=run_id, window_label="opening", trading_day=_FROZEN_NOW.date(),
        config_hash="cfg", started_at=_FROZEN_NOW,
    )
    persist_track_run(repo, run_id=run_id, result=result, completed_at=_FROZEN_NOW)
    session.commit()
    persist_track_run(repo, run_id=run_id, result=result, completed_at=_FROZEN_NOW)
    session.commit()

    track_runs = session.query(TrackRun).filter(TrackRun.run_id == run_id).all()
    assert len(track_runs) == 1
    decisions = session.query(JudgeDecision).filter(
        JudgeDecision.run_id == run_id
    ).all()
    assert len(decisions) == 1


def test_persist_skipped_track_marker(session: Session) -> None:
    repo = Repository(session)
    repo.create_run(
        run_id="win_skip", window_label="opening",
        trading_day=_FROZEN_NOW.date(), config_hash="cfg",
        started_at=_FROZEN_NOW,
    )
    persist_skipped_track(
        repo,
        run_id="win_skip",
        track_run_id=derive_track_run_id(
            window_run_id="win_skip", track_id="balanced", packet_id="pkt_x",
        ),
        track_id="balanced",
        packet_id="pkt_x",
        symbol="AAPL",
        started_at=_FROZEN_NOW,
        reason="paused for test",
    )
    session.commit()

    rows = session.query(TrackRun).filter(TrackRun.run_id == "win_skip").all()
    assert len(rows) == 1
    assert rows[0].status == "skipped_paused"
    assert rows[0].interrupt_reason == "paused for test"


def test_persist_failed_track_marker(session: Session) -> None:
    repo = Repository(session)
    repo.create_run(
        run_id="win_fail", window_label="opening",
        trading_day=_FROZEN_NOW.date(), config_hash="cfg",
        started_at=_FROZEN_NOW,
    )
    persist_failed_track(
        repo,
        run_id="win_fail",
        track_run_id=derive_track_run_id(
            window_run_id="win_fail", track_id="balanced", packet_id="pkt_x",
        ),
        track_id="balanced",
        packet_id="pkt_x",
        symbol="AAPL",
        started_at=_FROZEN_NOW,
        completed_at=_FROZEN_NOW + dt.timedelta(seconds=1),
        reason="boom",
    )
    session.commit()
    rows = session.query(TrackRun).filter(TrackRun.run_id == "win_fail").all()
    assert len(rows) == 1
    assert rows[0].status == "failed"
    assert rows[0].interrupt_reason == "boom"
