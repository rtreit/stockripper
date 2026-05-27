"""Per-judge regret scoring (spec §8.3, §19.4, §25 Phase 6)."""

from __future__ import annotations

import datetime as dt
import hashlib
from collections.abc import Mapping
from dataclasses import dataclass
from decimal import Decimal

from sqlalchemy import select
from sqlalchemy.orm import Session

from stockripper.db.models import DecisionAction, JudgeDecision
from stockripper.db.repository import Repository


@dataclass(frozen=True)
class JudgeRegretReport:
    judge_agent_id: str
    track_id: str
    as_of_date: dt.date
    selected_reward: Decimal
    best_alternative_reward: Decimal
    regret: Decimal
    observation_count: int


def _regret_id(
    *, judge_agent_id: str, track_id: str, as_of_date: dt.date,
) -> str:
    body = f"{judge_agent_id}\x00{track_id}\x00{as_of_date.isoformat()}"
    return "regret_" + hashlib.sha256(body.encode("utf-8")).hexdigest()[:24]


def _avg(values: list[Decimal]) -> Decimal:
    if not values:
        return Decimal("0")
    total = sum(values, start=Decimal("0"))
    return (total / Decimal(len(values))).quantize(Decimal("0.000001"))


def compute_judge_regret_for_track(
    *,
    session: Session,
    run_id: str,
    track_id: str,
    as_of_date: dt.date,
    rewards: Mapping[str, Decimal],
) -> JudgeRegretReport | None:
    """Compute a regret report for ``track_id`` within ``run_id``.

    Returns ``None`` when there is no JudgeDecision for the track in
    that run, or when no scored recommendations exist.
    """

    repo = Repository(session)
    decisions = list(
        session.execute(
            select(JudgeDecision)
            .where(JudgeDecision.run_id == run_id)
            .where(JudgeDecision.track_id == track_id),
        ).scalars(),
    )
    if not decisions:
        return None

    all_recs = repo.list_recommendations(run_id=run_id, track_id=track_id)
    if not all_recs:
        return None

    selected_rec_ids: set[str] = set()
    for dec in decisions:
        # MVP approximation: a recommendation is "selected" by the judge
        # if there is a non-HOLD DecisionAction for the same symbol on
        # the same track in this decision. Phase 7 will switch this to
        # the explicit contributing_recommendation_ids carried on the
        # judge's output_json envelope.
        actions = list(
            session.execute(
                select(DecisionAction)
                .where(DecisionAction.decision_id == dec.decision_id),
            ).scalars(),
        )
        for action in actions:
            for rec in all_recs:
                if rec.symbol.upper() != action.symbol.upper():
                    continue
                if rec.action in {"hold", "avoid"}:
                    continue
                selected_rec_ids.add(rec.recommendation_id)

    selected_rewards = [
        rewards[r.recommendation_id]
        for r in all_recs
        if r.recommendation_id in selected_rec_ids
        and r.recommendation_id in rewards
    ]
    all_rewards = [
        rewards[r.recommendation_id]
        for r in all_recs
        if r.recommendation_id in rewards
    ]
    if not all_rewards:
        return None

    selected_avg = _avg(selected_rewards)
    best_alt = max(all_rewards)
    regret = max(Decimal("0"), best_alt - selected_avg)
    return JudgeRegretReport(
        judge_agent_id=decisions[0].judge_agent_id,
        track_id=track_id,
        as_of_date=as_of_date,
        selected_reward=selected_avg.quantize(Decimal("0.000001")),
        best_alternative_reward=best_alt.quantize(Decimal("0.000001")),
        regret=regret.quantize(Decimal("0.000001")),
        observation_count=len(all_rewards),
    )


def persist_judge_regret_for_track(
    *,
    session: Session,
    report: JudgeRegretReport,
) -> None:
    Repository(session).upsert_judge_regret(
        regret_id=_regret_id(
            judge_agent_id=report.judge_agent_id,
            track_id=report.track_id,
            as_of_date=report.as_of_date,
        ),
        judge_agent_id=report.judge_agent_id,
        track_id=report.track_id,
        as_of_date=report.as_of_date,
        selected_reward=report.selected_reward,
        best_alternative_reward=report.best_alternative_reward,
        regret=report.regret,
        observation_count=report.observation_count,
    )


__all__ = (
    "JudgeRegretReport",
    "compute_judge_regret_for_track",
    "persist_judge_regret_for_track",
)
