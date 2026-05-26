"""Tests for the MCP-free reconciliation core (``apply_reconciliation``)."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.agents.reconciliation import (
    _track_from_client_order_id,
    apply_reconciliation,
)
from stockripper.db import Base, Repository, build_engine
from stockripper.tracks import DEFAULT_TRACKS, seed_default_tracks


@pytest.fixture
def session() -> Session:
    engine = build_engine("sqlite:///:memory:")
    Base.metadata.create_all(bind=engine)
    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    s = factory()
    seed_default_tracks(s)
    s.commit()
    return s


def test_apply_reconciliation_writes_one_snapshot_per_enabled_track(
    session: Session,
) -> None:
    account = {"equity": "100250.50", "cash": "98000.10", "buying_power": "196000.20"}
    report = apply_reconciliation(
        session,
        account_payload=account,
        orders_payload=[],
        captured_at=dt.datetime(2026, 5, 26, 14, tzinfo=dt.UTC),
    )
    session.commit()

    enabled = [t for t in DEFAULT_TRACKS if t.enabled]
    assert report.snapshots_written == len(enabled)
    assert report.account_equity == Decimal("100250.50")
    assert report.account_cash == Decimal("98000.10")
    assert report.buying_power == Decimal("196000.20")

    repo = Repository(session)
    for spec in enabled:
        snap = repo.latest_track_snapshot(spec.track_id)
        assert snap is not None, spec.track_id
        assert snap.equity == Decimal("100250.50")


def test_apply_reconciliation_upserts_orders_and_records_fills(
    session: Session,
) -> None:
    coid_cons = "cons_abcdef1234567890abcdef1234567890"
    coid_unattr = "ZZZZ_unattributed_legacy_paper_order"
    orders = [
        {
            "id": "alp_1",
            "client_order_id": coid_cons,
            "symbol": "AAPL",
            "side": "buy",
            "order_type": "market",
            "time_in_force": "day",
            "qty": "10",
            "status": "filled",
            "submitted_at": "2026-05-26T13:30:00Z",
            "filled_qty": "10",
            "filled_avg_price": "189.42",
            "filled_at": "2026-05-26T13:30:02Z",
        },
        {
            "id": "alp_2",
            "client_order_id": coid_unattr,
            "symbol": "MSFT",
            "side": "sell",
            "type": "limit",
            "time_in_force": "day",
            "qty": "5",
            "limit_price": "420.00",
            "status": "new",
            "submitted_at": "2026-05-26T13:31:00Z",
        },
    ]
    report = apply_reconciliation(
        session,
        account_payload={"equity": "100000", "cash": "100000"},
        orders_payload=orders,
        captured_at=dt.datetime(2026, 5, 26, 14, tzinfo=dt.UTC),
    )
    session.commit()

    assert report.orders_seen == 2
    assert report.fills_seen == 1

    # The cons_ prefix should resolve to the conservative track id.
    assert _track_from_client_order_id(coid_cons) == "conservative"
    # Unknown prefix falls through to unattributed.
    assert _track_from_client_order_id(coid_unattr) == "unattributed"


def test_apply_reconciliation_is_idempotent(session: Session) -> None:
    coid = "cons_abcdef1234567890abcdef1234567890"
    payload = {
        "id": "alp_1",
        "client_order_id": coid,
        "symbol": "AAPL",
        "side": "buy",
        "order_type": "market",
        "time_in_force": "day",
        "qty": "10",
        "status": "accepted",
        "submitted_at": "2026-05-26T13:30:00Z",
    }
    apply_reconciliation(
        session,
        account_payload={"equity": "100000", "cash": "100000"},
        orders_payload=[payload],
        captured_at=dt.datetime(2026, 5, 26, 14, tzinfo=dt.UTC),
    )
    session.commit()
    apply_reconciliation(
        session,
        account_payload={"equity": "100000", "cash": "100000"},
        orders_payload=[payload],
        captured_at=dt.datetime(2026, 5, 26, 14, tzinfo=dt.UTC),
    )
    session.commit()

    # Should still be exactly one Order row.
    from stockripper.db.models import Order

    rows = session.query(Order).all()
    assert len(rows) == 1
    assert rows[0].client_order_id == coid
