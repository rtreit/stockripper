"""Tests for the judge regret scoring engine (Phase 6)."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.db import Base, Repository, build_engine
from stockripper.db.models import DecisionAction, JudgeDecision, Recommendation
from stockripper.scoring.judge_regret import (
    compute_judge_regret_for_track,
    persist_judge_regret_for_track,
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


def _seed_world(
    session: Session,
    *,
    track_id: str,
    selected_symbol: str,
    other_symbols: list[str],
) -> str:
    repo = Repository(session)
    run_id = "run_jr1"
    repo.create_run(
        run_id=run_id,
        window_label="opening",
        trading_day=dt.date(2026, 5, 30),
        config_hash="cfg",
        started_at=dt.datetime(2026, 5, 30, 14, tzinfo=dt.UTC),
    )

    for i, sym in enumerate([selected_symbol, *other_symbols]):
        session.add(
            Recommendation(
                recommendation_id=f"rec_{i}",
                run_id=run_id,
                track_id=track_id,
                agent_id=f"agent_{i}",
                symbol=sym,
                instrument_type="equity",
                action="buy",
                conviction=Decimal("0.6"),
                time_horizon_days=5,
                thesis="t",
                schema_valid=True,
                created_at=dt.datetime(2026, 5, 30, 14, tzinfo=dt.UTC),
            ),
        )

    decision = JudgeDecision(
        decision_id="dec_1",
        run_id=run_id,
        track_id=track_id,
        judge_agent_id="judge_calmar",
        portfolio_posture="net_long",
        created_at=dt.datetime(2026, 5, 30, 14, tzinfo=dt.UTC),
    )
    session.add(decision)
    session.flush()
    session.add(
        DecisionAction(
            action_id="act_1",
            decision_id="dec_1",
            track_id=track_id,
            symbol=selected_symbol,
            instrument_type="equity",
            action="buy",
            target_notional_usd=Decimal("1000"),
        ),
    )
    session.flush()
    return run_id


def test_returns_none_when_no_judge_decision(session: Session) -> None:
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    report = compute_judge_regret_for_track(
        session=session,
        run_id="run_doesnotexist",
        track_id=track_id,
        as_of_date=dt.date(2026, 5, 30),
        rewards={},
    )
    assert report is None


def test_regret_is_zero_when_judge_picked_best(session: Session) -> None:
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    run_id = _seed_world(
        session, track_id=track_id, selected_symbol="BEST",
        other_symbols=["AAA", "BBB"],
    )
    rewards = {
        "rec_0": Decimal("0.10"),  # BEST — selected
        "rec_1": Decimal("0.02"),
        "rec_2": Decimal("-0.01"),
    }
    report = compute_judge_regret_for_track(
        session=session, run_id=run_id, track_id=track_id,
        as_of_date=dt.date(2026, 5, 30), rewards=rewards,
    )
    assert report is not None
    assert report.judge_agent_id == "judge_calmar"
    assert report.selected_reward == Decimal("0.100000")
    assert report.best_alternative_reward == Decimal("0.100000")
    assert report.regret == Decimal("0.000000")
    assert report.observation_count == 3


def test_regret_positive_when_alternative_better(session: Session) -> None:
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    run_id = _seed_world(
        session, track_id=track_id, selected_symbol="MEH",
        other_symbols=["GREAT", "OK"],
    )
    rewards = {
        "rec_0": Decimal("0.02"),  # MEH — selected
        "rec_1": Decimal("0.15"),  # GREAT — better, ignored
        "rec_2": Decimal("0.05"),
    }
    report = compute_judge_regret_for_track(
        session=session, run_id=run_id, track_id=track_id,
        as_of_date=dt.date(2026, 5, 30), rewards=rewards,
    )
    assert report is not None
    assert report.selected_reward == Decimal("0.020000")
    assert report.best_alternative_reward == Decimal("0.150000")
    assert report.regret == Decimal("0.130000")


def test_persist_judge_regret_roundtrips(session: Session) -> None:
    track_id = Repository(session).list_strategy_tracks()[0].track_id
    run_id = _seed_world(
        session, track_id=track_id, selected_symbol="X",
        other_symbols=["Y"],
    )
    rewards = {
        "rec_0": Decimal("0.05"),
        "rec_1": Decimal("0.10"),
    }
    report = compute_judge_regret_for_track(
        session=session, run_id=run_id, track_id=track_id,
        as_of_date=dt.date(2026, 5, 30), rewards=rewards,
    )
    assert report is not None
    persist_judge_regret_for_track(session=session, report=report)
    rows = Repository(session).list_judge_regret(track_id=track_id)
    assert len(rows) == 1
    assert rows[0].regret == Decimal("0.050000")
