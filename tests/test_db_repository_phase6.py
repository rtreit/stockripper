"""Tests for Phase 6 repository methods (agent_scores, leaderboard, regret)."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

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


def _track(session: Session) -> str:
    return Repository(session).list_strategy_tracks()[0].track_id


def test_upsert_agent_score_is_idempotent(session: Session) -> None:
    repo = Repository(session)
    track_id = _track(session)
    row = repo.upsert_agent_score(
        score_id="score_test",
        agent_id="momentum_breakout",
        track_id=track_id,
        as_of_date=dt.date(2026, 5, 30),
        reward_score=Decimal("0.0123"),
        observation_count=4,
    )
    assert row.reward_score == Decimal("0.0123")

    row2 = repo.upsert_agent_score(
        score_id="score_test",
        agent_id="momentum_breakout",
        track_id=track_id,
        as_of_date=dt.date(2026, 5, 30),
        reward_score=Decimal("-0.005"),
        observation_count=5,
    )
    assert row2.score_id == row.score_id
    assert row2.reward_score == Decimal("-0.005")
    assert row2.observation_count == 5


def test_list_agent_scores_filters(session: Session) -> None:
    repo = Repository(session)
    track_id = _track(session)
    repo.upsert_agent_score(
        score_id="s1", agent_id="a1", track_id=track_id,
        as_of_date=dt.date(2026, 5, 30),
        reward_score=Decimal("0.01"), observation_count=1,
    )
    repo.upsert_agent_score(
        score_id="s2", agent_id="a2", track_id=track_id,
        as_of_date=dt.date(2026, 5, 30),
        reward_score=Decimal("0.05"), observation_count=2,
    )
    rows = repo.list_agent_scores(track_id=track_id)
    assert len(rows) == 2
    # ordered by reward desc within same date
    assert rows[0].reward_score >= rows[1].reward_score

    only_a1 = repo.list_agent_scores(track_id=track_id, agent_id="a1")
    assert len(only_a1) == 1
    assert only_a1[0].agent_id == "a1"


def test_upsert_leaderboard_entry_is_idempotent(session: Session) -> None:
    repo = Repository(session)
    track_id = _track(session)
    row = repo.upsert_leaderboard_entry(
        leaderboard_id="lb_a",
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
        track_id=track_id,
        cumulative_return_pct=Decimal("0.07"),
        rank=1,
    )
    assert row.rank == 1
    row2 = repo.upsert_leaderboard_entry(
        leaderboard_id="lb_a",
        window_start=dt.date(2026, 5, 1),
        window_end=dt.date(2026, 5, 30),
        track_id=track_id,
        cumulative_return_pct=Decimal("0.11"),
        rank=2,
    )
    assert row2.leaderboard_id == "lb_a"
    assert row2.cumulative_return_pct == Decimal("0.11")
    assert row2.rank == 2


def test_upsert_judge_regret_is_idempotent(session: Session) -> None:
    repo = Repository(session)
    track_id = _track(session)
    row = repo.upsert_judge_regret(
        regret_id="reg_a",
        judge_agent_id="judge_calmar",
        track_id=track_id,
        as_of_date=dt.date(2026, 5, 30),
        selected_reward=Decimal("0.02"),
        best_alternative_reward=Decimal("0.05"),
        regret=Decimal("0.03"),
        observation_count=3,
    )
    assert row.regret == Decimal("0.03")
    row2 = repo.upsert_judge_regret(
        regret_id="reg_a",
        judge_agent_id="judge_calmar",
        track_id=track_id,
        as_of_date=dt.date(2026, 5, 30),
        selected_reward=Decimal("0.04"),
        best_alternative_reward=Decimal("0.05"),
        regret=Decimal("0.01"),
        observation_count=4,
    )
    assert row2.regret_id == "reg_a"
    assert row2.regret == Decimal("0.01")
