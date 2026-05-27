"""Tests for the Phase-4 Repository methods (track_runs, agent_runs, control plane)."""

from __future__ import annotations

import datetime as dt

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.db import Base, Repository, build_engine
from stockripper.tracks import seed_default_tracks


@pytest.fixture
def session() -> Session:
    engine = build_engine("sqlite:///:memory:")
    Base.metadata.create_all(bind=engine)
    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    s = factory()
    seed_default_tracks(s)
    s.commit()
    return s


def _utc(year: int = 2026, month: int = 5, day: int = 28) -> dt.datetime:
    return dt.datetime(year, month, day, 15, 0, 0, tzinfo=dt.UTC)


def test_create_run_is_idempotent(session: Session) -> None:
    repo = Repository(session)
    when = _utc()
    a = repo.create_run(
        run_id="win_test1", window_label="opening",
        trading_day=when.date(), config_hash="cfg1", started_at=when,
    )
    b = repo.create_run(
        run_id="win_test1", window_label="opening",
        trading_day=when.date(), config_hash="cfg1", started_at=when,
    )
    assert a is b
    assert a.status == "running"


def test_complete_run_sets_status_and_completed_at(session: Session) -> None:
    repo = Repository(session)
    when = _utc()
    repo.create_run(
        run_id="win_complete", window_label="opening",
        trading_day=when.date(), config_hash="cfg", started_at=when,
    )
    finished = when + dt.timedelta(seconds=12)
    run = repo.complete_run(run_id="win_complete", status="ok", completed_at=finished)
    assert run.status == "ok"
    assert run.completed_at == finished


def test_complete_run_raises_for_unknown_id(session: Session) -> None:
    repo = Repository(session)
    with pytest.raises(KeyError):
        repo.complete_run(run_id="nope", status="ok")


def test_upsert_track_run_creates_then_updates(session: Session) -> None:
    repo = Repository(session)
    when = _utc()
    repo.create_run(
        run_id="win_tr1", window_label="opening",
        trading_day=when.date(), config_hash="cfg", started_at=when,
    )
    created = repo.upsert_track_run(
        track_run_id="trk_aaaa",
        run_id="win_tr1",
        track_id="balanced",
        packet_id="pkt_x",
        symbol="AAPL",
        status="ok",
        started_at=when,
    )
    session.flush()
    updated = repo.upsert_track_run(
        track_run_id="trk_aaaa",
        run_id="win_tr1",
        track_id="balanced",
        packet_id="pkt_x",
        symbol="AAPL",
        status="partial",
        started_at=when,
        completed_at=when + dt.timedelta(seconds=5),
        interrupt_reason="qa",
    )
    assert created is updated
    assert updated.status == "partial"
    assert updated.interrupt_reason == "qa"


def test_kill_switch_engage_release_cycle(session: Session) -> None:
    repo = Repository(session)
    initial = repo.get_kill_switch()
    assert initial.engaged is False
    when = _utc()
    state = repo.engage_kill_switch(
        reason="rate-limited by exchange", engaged_by="op", when=when,
    )
    assert state.engaged is True
    assert state.reason == "rate-limited by exchange"
    assert state.engaged_at == when
    released = repo.release_kill_switch(when=when + dt.timedelta(seconds=30))
    assert released.engaged is False
    assert released.reason is None


def test_pause_track_then_resume(session: Session) -> None:
    repo = Repository(session)
    when = _utc()
    paused = repo.pause_track(
        track_id="yolo", reason="risk_review", paused_by="op", when=when,
    )
    assert paused.paused is True
    assert "yolo" in repo.list_paused_track_ids()

    resumed = repo.resume_track(track_id="yolo", when=when + dt.timedelta(minutes=1))
    assert resumed.paused is False
    assert repo.list_paused_track_ids() == []


def test_list_track_pause_states_returns_history(session: Session) -> None:
    repo = Repository(session)
    when = _utc()
    repo.pause_track(track_id="yolo", reason="r1", when=when)
    repo.pause_track(track_id="aggressive", reason="r2", when=when)
    rows = repo.list_track_pause_states()
    track_ids = {r.track_id for r in rows}
    assert track_ids == {"yolo", "aggressive"}
