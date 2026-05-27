"""Tests for the per-track leaderboard scoring engine (Phase 6)."""

from __future__ import annotations

import datetime as dt
import hashlib
from decimal import Decimal

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.db import Base, Repository, build_engine
from stockripper.db.models import TrackSnapshot
from stockripper.scoring.leaderboard import (
    compute_leaderboard,
    persist_leaderboard,
)
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


def _snap(track_id: str, day: dt.date, equity: Decimal) -> TrackSnapshot:
    body = f"{track_id}\x00{day.isoformat()}\x00{equity}"
    snap_id = "snap_" + hashlib.sha256(body.encode()).hexdigest()[:24]
    captured = dt.datetime.combine(day, dt.time(20, 0), tzinfo=dt.UTC)
    return TrackSnapshot(
        snapshot_id=snap_id,
        track_id=track_id,
        captured_at=captured,
        equity=equity,
        cash=Decimal("0"),
    )


def _seed_snapshots(
    session: Session, *, track_id: str, equities: list[Decimal],
) -> None:
    start = dt.date(2026, 5, 25)
    for i, eq in enumerate(equities):
        session.add(_snap(track_id, start + dt.timedelta(days=i), eq))
    session.flush()


def test_compute_leaderboard_returns_empty_when_no_snapshots(
    session: Session,
) -> None:
    rows = compute_leaderboard(
        session=session,
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
    )
    assert rows == []


def test_compute_leaderboard_cumulative_return(session: Session) -> None:
    tracks = Repository(session).list_strategy_tracks()
    t = tracks[0].track_id
    _seed_snapshots(
        session, track_id=t,
        equities=[
            Decimal("100000"),
            Decimal("101000"),
            Decimal("102000"),
            Decimal("110000"),
        ],
    )
    rows = compute_leaderboard(
        session=session,
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
        track_ids=(t,),
    )
    assert len(rows) == 1
    m = rows[0]
    assert m.track_id == t
    assert m.cumulative_return_pct == Decimal("0.100000")
    # Win rate: 3 positive returns out of 3 -> 1.0
    assert m.win_rate == Decimal("1.000000")
    assert m.max_drawdown_pct == Decimal("0")
    assert m.sharpe is not None and m.sharpe > 0


def test_compute_leaderboard_max_drawdown(session: Session) -> None:
    tracks = Repository(session).list_strategy_tracks()
    t = tracks[0].track_id
    _seed_snapshots(
        session, track_id=t,
        equities=[
            Decimal("100000"),
            Decimal("110000"),
            Decimal("88000"),
            Decimal("99000"),
        ],
    )
    rows = compute_leaderboard(
        session=session,
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
        track_ids=(t,),
    )
    m = rows[0]
    # peak=110000, trough=88000 -> (110000-88000)/110000 = 0.2
    assert m.max_drawdown_pct == Decimal("0.200000")


def test_compute_leaderboard_handles_single_snapshot(session: Session) -> None:
    tracks = Repository(session).list_strategy_tracks()
    t = tracks[0].track_id
    _seed_snapshots(
        session, track_id=t, equities=[Decimal("100000")],
    )
    rows = compute_leaderboard(
        session=session,
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
        track_ids=(t,),
    )
    assert len(rows) == 1
    m = rows[0]
    assert m.cumulative_return_pct == Decimal("0.000000")
    assert m.sharpe is None  # need >=2 returns
    assert m.win_rate is None


def test_persist_leaderboard_assigns_ranks(session: Session) -> None:
    tracks = Repository(session).list_strategy_tracks()
    assert len(tracks) >= 2
    t1, t2 = tracks[0].track_id, tracks[1].track_id
    _seed_snapshots(
        session, track_id=t1,
        equities=[Decimal("100000"), Decimal("105000")],
    )
    _seed_snapshots(
        session, track_id=t2,
        equities=[Decimal("100000"), Decimal("110000")],
    )
    metrics = compute_leaderboard(
        session=session,
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
        track_ids=(t1, t2),
    )
    persist_leaderboard(
        session=session,
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
        metrics=metrics,
    )
    rows = Repository(session).list_leaderboard(
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
    )
    by_track = {r.track_id: r for r in rows}
    assert by_track[t2].rank == 1  # higher cumulative return
    assert by_track[t1].rank == 2
