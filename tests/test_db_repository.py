"""SQLite round-trip tests for ORM models, repository, and track seeding."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.db import Base, Repository, build_engine
from stockripper.risk import DEFAULT_RISK_POLICIES
from stockripper.tracks import DEFAULT_TRACKS, seed_default_tracks


@pytest.fixture
def session() -> Session:
    """Spin up an in-memory SQLite DB with the full Phase-1 schema."""

    engine = build_engine("sqlite:///:memory:")
    Base.metadata.create_all(bind=engine)
    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    return factory()


def test_seed_default_tracks_writes_all(session: Session) -> None:
    policies, tracks = seed_default_tracks(session)
    session.commit()
    assert policies == len(DEFAULT_RISK_POLICIES)
    assert tracks == len(DEFAULT_TRACKS)

    repo = Repository(session)
    assert len(repo.list_strategy_tracks()) == len(DEFAULT_TRACKS)
    yolo = repo.get_strategy_track("yolo")
    assert yolo is not None
    assert yolo.risk_policy_id == "rp_yolo"


def test_seed_default_tracks_is_idempotent(session: Session) -> None:
    seed_default_tracks(session)
    session.commit()
    seed_default_tracks(session)
    session.commit()
    repo = Repository(session)
    assert len(repo.list_strategy_tracks()) == len(DEFAULT_TRACKS)


def test_upsert_order_round_trip(session: Session) -> None:
    seed_default_tracks(session)
    session.commit()

    repo = Repository(session)
    payload = {
        "id": "alp_123",
        "client_order_id": "cons_abcdef1234567890abcdef1234567890",
        "symbol": "aapl",
        "side": "buy",
        "order_type": "market",
        "time_in_force": "day",
        "qty": "10",
        "status": "accepted",
        "submitted_at": "2026-05-26T13:30:00Z",
    }
    order = repo.upsert_order_from_alpaca(track_id="conservative", alpaca_order=payload)
    session.commit()

    assert order.symbol == "AAPL"
    assert order.alpaca_order_id == "alp_123"
    assert order.requested_qty == Decimal("10")

    # Update path: change status, re-upsert.
    payload["status"] = "filled"
    payload["filled_qty"] = "10"
    payload["filled_avg_price"] = "189.42"
    payload["filled_at"] = "2026-05-26T13:30:05Z"
    order2 = repo.upsert_order_from_alpaca(track_id="conservative", alpaca_order=payload)
    session.commit()

    assert order2.local_order_id == order.local_order_id
    assert order2.status == "filled"


def test_record_track_snapshot_latest(session: Session) -> None:
    seed_default_tracks(session)
    session.commit()
    repo = Repository(session)

    t0 = dt.datetime(2026, 5, 26, 13, 30, tzinfo=dt.UTC)
    t1 = dt.datetime(2026, 5, 26, 13, 35, tzinfo=dt.UTC)
    repo.record_track_snapshot(
        snapshot_id="snap_cons_1",
        track_id="conservative",
        captured_at=t0,
        equity=Decimal("100000"),
        cash=Decimal("100000"),
    )
    repo.record_track_snapshot(
        snapshot_id="snap_cons_2",
        track_id="conservative",
        captured_at=t1,
        equity=Decimal("100123.45"),
        cash=Decimal("90000"),
    )
    session.commit()

    latest = repo.latest_track_snapshot("conservative")
    assert latest is not None
    assert latest.snapshot_id == "snap_cons_2"
    assert latest.equity == Decimal("100123.45")
