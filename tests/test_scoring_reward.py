"""Tests for the reward scoring engine (Phase 6)."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.db import Base, Repository, build_engine
from stockripper.scoring.reward import (
    StaticPriceProvider,
    aggregate_rewards,
    score_recommendations_for_window,
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


def _seed_run(session: Session, *, run_id: str = "run_t1") -> str:
    Repository(session).create_run(
        run_id=run_id,
        window_label="opening",
        trading_day=dt.date(2026, 5, 30),
        config_hash="cfg",
        started_at=dt.datetime(2026, 5, 30, 14, tzinfo=dt.UTC),
    )
    return run_id


def _seed_recommendation(
    session: Session,
    *,
    run_id: str,
    track_id: str,
    agent_id: str,
    rec_id: str,
    symbol: str,
    action: str,
    horizon: int = 5,
) -> None:
    from stockripper.db.models import Recommendation

    rec = Recommendation(
        recommendation_id=rec_id,
        run_id=run_id,
        track_id=track_id,
        agent_id=agent_id,
        symbol=symbol,
        instrument_type="equity",
        action=action,
        conviction=Decimal("0.7"),
        time_horizon_days=horizon,
        thesis=f"{action} thesis",
        schema_valid=True,
        created_at=dt.datetime(2026, 5, 30, 14, tzinfo=dt.UTC),
    )
    session.add(rec)
    session.flush()


def test_aggregate_rewards_handles_empty() -> None:
    assert aggregate_rewards([]) == Decimal("0")


def test_aggregate_rewards_averages() -> None:
    out = aggregate_rewards([Decimal("0.1"), Decimal("-0.05"), Decimal("0.03")])
    # (0.1 - 0.05 + 0.03) / 3 = 0.026666...
    assert out == Decimal("0.026667")


def test_score_buy_recommendation_uses_excess_over_benchmark(
    session: Session,
) -> None:
    run_id = _seed_run(session)
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    _seed_recommendation(
        session, run_id=run_id, track_id=track_id, agent_id="agent_buy",
        rec_id="rec_buy", symbol="AAPL", action="buy",
    )
    base_date = dt.date(2026, 5, 30)
    provider = StaticPriceProvider(
        table={
            ("AAPL", base_date, 5): Decimal("0.10"),  # +10%
            ("SPY", base_date, 5): Decimal("0.03"),   # +3%
        },
    )
    rows = score_recommendations_for_window(
        session=session,
        run_id=run_id,
        as_of_date=base_date,
        price_provider=provider,
    )
    assert len(rows) == 1
    agent_id, t_id, reward, n = rows[0]
    assert agent_id == "agent_buy"
    assert t_id == track_id
    assert reward == Decimal("0.070000")
    assert n == 1


def test_score_sell_recommendation_flips_sign(session: Session) -> None:
    run_id = _seed_run(session)
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    _seed_recommendation(
        session, run_id=run_id, track_id=track_id, agent_id="agent_short",
        rec_id="rec_short", symbol="LOSER", action="short",
    )
    base_date = dt.date(2026, 5, 30)
    provider = StaticPriceProvider(
        table={
            ("LOSER", base_date, 5): Decimal("-0.05"),
            ("SPY", base_date, 5): Decimal("0.02"),
        },
    )
    rows = score_recommendations_for_window(
        session=session, run_id=run_id, as_of_date=base_date,
        price_provider=provider,
    )
    agent_id, _, reward, _ = rows[0]
    assert agent_id == "agent_short"
    # excess = -0.05 - 0.02 = -0.07; sell flips sign -> +0.07
    assert reward == Decimal("0.070000")


def test_score_hold_recommendation_is_zero(session: Session) -> None:
    run_id = _seed_run(session)
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    _seed_recommendation(
        session, run_id=run_id, track_id=track_id, agent_id="agent_hold",
        rec_id="rec_hold", symbol="AAPL", action="hold",
    )
    base_date = dt.date(2026, 5, 30)
    provider = StaticPriceProvider(
        table={
            ("AAPL", base_date, 5): Decimal("0.10"),
            ("SPY", base_date, 5): Decimal("0.03"),
        },
    )
    rows = score_recommendations_for_window(
        session=session, run_id=run_id, as_of_date=base_date,
        price_provider=provider,
    )
    assert rows == [("agent_hold", track_id, Decimal("0.000000"), 1)]


def test_score_skips_recommendations_with_missing_prices(session: Session) -> None:
    run_id = _seed_run(session)
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    _seed_recommendation(
        session, run_id=run_id, track_id=track_id, agent_id="agent_a",
        rec_id="rec_a", symbol="UNKNOWN", action="buy",
    )
    base_date = dt.date(2026, 5, 30)
    provider = StaticPriceProvider(table={})  # no entries at all
    rows = score_recommendations_for_window(
        session=session, run_id=run_id, as_of_date=base_date,
        price_provider=provider,
    )
    assert rows == []


def test_score_rolls_up_multiple_recs_same_agent(session: Session) -> None:
    run_id = _seed_run(session)
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    _seed_recommendation(
        session, run_id=run_id, track_id=track_id, agent_id="agent_x",
        rec_id="rec_1", symbol="AAA", action="buy",
    )
    _seed_recommendation(
        session, run_id=run_id, track_id=track_id, agent_id="agent_x",
        rec_id="rec_2", symbol="BBB", action="buy",
    )
    base_date = dt.date(2026, 5, 30)
    provider = StaticPriceProvider(
        table={
            ("AAA", base_date, 5): Decimal("0.10"),
            ("BBB", base_date, 5): Decimal("0.00"),
            ("SPY", base_date, 5): Decimal("0.03"),
        },
    )
    rows = score_recommendations_for_window(
        session=session, run_id=run_id, as_of_date=base_date,
        price_provider=provider,
    )
    assert len(rows) == 1
    agent_id, _, reward, n = rows[0]
    assert agent_id == "agent_x"
    # avg of +0.07 and -0.03 -> 0.02
    assert reward == Decimal("0.020000")
    assert n == 2
    # Verify AgentScore row was upserted
    scores = Repository(session).list_agent_scores(track_id=track_id)
    assert len(scores) == 1
    assert scores[0].reward_score == Decimal("0.020000")
    assert scores[0].observation_count == 2
