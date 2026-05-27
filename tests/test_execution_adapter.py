"""Tests for the Phase-5 execution adapter (spec §16.1, §25 Phase 5)."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.agents.schemas import (
    ActionItem,
    ActionOrderType,
    OrderSide,
    RecommendationInstrument,
)
from stockripper.db import Base, Repository, build_engine
from stockripper.db.models import DecisionAction, Fill, JudgeDecision, Order, Run
from stockripper.execution.adapter import (
    ExecutionAdapter,
    MockBrokerClient,
    SubmissionStatus,
)
from stockripper.tracks import seed_default_tracks

_FROZEN_NOW = dt.datetime(2026, 5, 28, 14, 30, 0, tzinfo=dt.UTC)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
@pytest.fixture
def session_factory() -> sessionmaker[Session]:
    engine = build_engine("sqlite:///:memory:")
    Base.metadata.create_all(bind=engine)
    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    with factory() as s:
        seed_default_tracks(s)
        s.commit()
    return factory


def _seed_run_decision_action(
    factory: sessionmaker[Session],
    *,
    action_id: str = "act_test",
    track_id: str = "balanced",
    symbol: str = "AAPL",
    side: OrderSide = OrderSide.BUY,
    instrument: RecommendationInstrument = RecommendationInstrument.EQUITY,
    target_notional_usd: Decimal | None = Decimal("1000"),
    target_pct_equity: Decimal | None = None,
) -> None:
    """Pre-create a Run + JudgeDecision + DecisionAction row so the
    universal audit-completeness floor passes."""

    with factory() as s:
        run = Run(
            run_id="run_test",
            trading_day=_FROZEN_NOW.date(),
            window_label="adhoc",
            status="ok",
            started_at=_FROZEN_NOW,
            completed_at=_FROZEN_NOW,
            config_hash="cfg-test",
        )
        s.add(run)
        decision = JudgeDecision(
            decision_id="dec_test",
            run_id="run_test",
            track_id=track_id,
            judge_agent_id="judge_x",
            portfolio_posture="cash",
            created_at=_FROZEN_NOW,
        )
        s.add(decision)
        s.flush()
        action_row = DecisionAction(
            action_id=action_id,
            decision_id="dec_test",
            track_id=track_id,
            symbol=symbol,
            instrument_type=instrument.value,
            action=side.value,
            target_notional_usd=target_notional_usd,
            target_pct_equity=target_pct_equity,
            order_type=ActionOrderType.MARKET.value,
            time_in_force="day",
            rationale="test",
        )
        s.add(action_row)
        s.commit()


def _action(
    *,
    action_id: str = "act_test",
    track_id: str = "balanced",
    symbol: str = "AAPL",
    side: OrderSide = OrderSide.BUY,
    instrument: RecommendationInstrument = RecommendationInstrument.EQUITY,
    target_notional_usd: Decimal | None = Decimal("1000"),
    target_pct_equity: Decimal | None = None,
) -> ActionItem:
    kwargs: dict[str, object] = {
        "action_id": action_id,
        "track_id": track_id,
        "symbol": symbol,
        "instrument": instrument,
        "side": side,
        "order_type": ActionOrderType.MARKET,
        "rationale": "test",
    }
    if target_notional_usd is not None:
        kwargs["target_notional_usd"] = target_notional_usd
    if target_pct_equity is not None:
        kwargs["target_pct_equity"] = target_pct_equity
    return ActionItem(**kwargs)  # type: ignore[arg-type]


# ---------------------------------------------------------------------------
# Happy path
# ---------------------------------------------------------------------------
def test_adapter_submits_through_mock_broker(
    session_factory: sessionmaker[Session],
) -> None:
    _seed_run_decision_action(session_factory)
    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    result = adapter.submit_action(_action())

    assert result.status == SubmissionStatus.SUBMITTED
    assert result.client_order_id is not None
    assert result.local_order_id is not None
    assert result.risk_status_label == "approved"

    with session_factory() as s:
        repo = Repository(s)
        orders = repo.list_orders_for_track(track_id="balanced")
        assert len(orders) == 1
        order = orders[0]
        assert order.client_order_id == result.client_order_id
        assert order.symbol == "AAPL"
        assert order.status == "filled"
        # MockBroker synthesizes a fill -> Fill row present.
        fills = s.query(Fill).filter(Fill.local_order_id == order.local_order_id).all()
        assert len(fills) == 1
        assert fills[0].filled_avg_price > 0
        # The action row's risk_status should now be 'approved'.
        action_row = s.get(DecisionAction, "act_test")
        assert action_row is not None
        assert action_row.risk_status == "approved"


# ---------------------------------------------------------------------------
# Idempotency
# ---------------------------------------------------------------------------
def test_adapter_duplicate_collapses_via_client_order_id(
    session_factory: sessionmaker[Session],
) -> None:
    _seed_run_decision_action(session_factory)
    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    first = adapter.submit_action(_action())
    second = adapter.submit_action(_action())

    assert first.status == SubmissionStatus.SUBMITTED
    assert second.status == SubmissionStatus.DUPLICATE
    assert first.client_order_id == second.client_order_id
    assert first.local_order_id == second.local_order_id

    with session_factory() as s:
        orders = s.query(Order).all()
        assert len(orders) == 1  # No duplicate row.


# ---------------------------------------------------------------------------
# Floor enforcement
# ---------------------------------------------------------------------------
def test_adapter_blocked_by_mid_window_kill_switch(
    session_factory: sessionmaker[Session],
) -> None:
    _seed_run_decision_action(session_factory)

    # Kill switch engaged AFTER the run/decision/action rows already exist.
    with session_factory() as s:
        repo = Repository(s)
        repo.engage_kill_switch(reason="ops_drill", engaged_by="test")
        s.commit()

    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    result = adapter.submit_action(_action())

    assert result.status == SubmissionStatus.REJECTED_FLOOR
    assert result.risk_status_label == "rejected_floor:floor_kill_switch"
    assert result.local_order_id is None

    with session_factory() as s:
        assert s.query(Order).count() == 0
        action_row = s.get(DecisionAction, "act_test")
        assert action_row is not None
        assert action_row.risk_status == "rejected_floor:floor_kill_switch"


def test_adapter_blocked_by_track_pause(
    session_factory: sessionmaker[Session],
) -> None:
    _seed_run_decision_action(session_factory)
    with session_factory() as s:
        repo = Repository(s)
        repo.pause_track(track_id="balanced", reason="manual")
        s.commit()
    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    result = adapter.submit_action(_action())
    assert result.status == SubmissionStatus.REJECTED_FLOOR
    assert "track_paused" in result.risk_status_label


def test_adapter_rejects_unknown_track(
    session_factory: sessionmaker[Session],
) -> None:
    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    # No matching strategy_tracks row -> floor rejection.
    result = adapter.submit_action(
        _action(track_id="ghost_track"),
    )
    assert result.status == SubmissionStatus.REJECTED_FLOOR
    assert "unknown_track:ghost_track" in result.risk_status_label


# ---------------------------------------------------------------------------
# Risk gate enforcement
# ---------------------------------------------------------------------------
def test_adapter_persists_risk_status_on_gate_rejection(
    session_factory: sessionmaker[Session],
) -> None:
    # Balanced cap is 7% of $100k = $7k. $10k breaches it.
    _seed_run_decision_action(
        session_factory, target_notional_usd=Decimal("10000"),
    )
    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    result = adapter.submit_action(
        _action(target_notional_usd=Decimal("10000")),
    )
    assert result.status == SubmissionStatus.REJECTED_RISK
    assert "rt_position_pct_equity" in result.risk_status_label
    assert result.local_order_id is None

    with session_factory() as s:
        # Nothing was submitted to the broker.
        assert s.query(Order).count() == 0
        action_row = s.get(DecisionAction, "act_test")
        assert action_row is not None
        assert action_row.risk_status is not None
        assert "rt_position_pct_equity" in action_row.risk_status


def test_adapter_rejects_short_on_no_short_policy(
    session_factory: sessionmaker[Session],
) -> None:
    _seed_run_decision_action(
        session_factory, side=OrderSide.SELL_SHORT,
    )
    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    result = adapter.submit_action(_action(side=OrderSide.SELL_SHORT))
    assert result.status == SubmissionStatus.REJECTED_RISK
    assert "rt_short_disallowed" in result.risk_status_label


# ---------------------------------------------------------------------------
# Deterministic client_order_id
# ---------------------------------------------------------------------------
def test_same_intent_in_different_window_gets_different_coid(
    session_factory: sessionmaker[Session],
) -> None:
    _seed_run_decision_action(session_factory)
    adapter_a = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    adapter_b = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="midday",
    )
    res_a = adapter_a.submit_action(_action())
    res_b = adapter_b.submit_action(_action())
    assert res_a.client_order_id != res_b.client_order_id
    assert res_a.status == SubmissionStatus.SUBMITTED
    # The second submission is in a different window so it is a NEW order, not a duplicate.
    assert res_b.status == SubmissionStatus.SUBMITTED


def test_same_intent_same_window_collapses(
    session_factory: sessionmaker[Session],
) -> None:
    _seed_run_decision_action(session_factory)
    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    first = adapter.submit_action(_action())
    second = adapter.submit_action(_action())
    assert first.client_order_id == second.client_order_id
