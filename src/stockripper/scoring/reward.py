"""Per-recommendation reward scoring (spec §8.2 / §25 Phase 6)."""

from __future__ import annotations

import datetime as dt
import hashlib
import logging
from collections.abc import Iterable
from dataclasses import dataclass
from decimal import Decimal
from typing import Final, Protocol

from sqlalchemy.orm import Session

from stockripper.db.repository import Repository

LOG: Final = logging.getLogger(__name__)

_BUY_ACTIONS: Final[frozenset[str]] = frozenset({"buy", "buy_to_open_option"})
_SELL_ACTIONS: Final[frozenset[str]] = frozenset(
    {"sell", "short", "cover", "sell_to_open_option"},
)
_NEUTRAL_ACTIONS: Final[frozenset[str]] = frozenset({"hold", "avoid", "multi_leg"})

_BENCHMARK_SYMBOL: Final[str] = "SPY"


@dataclass(frozen=True)
class PriceObservation:
    """One realized (symbol, as_of_date, return_pct) observation."""

    symbol: str
    as_of_date: dt.date
    return_pct: Decimal


class PriceProvider(Protocol):
    """Indirection between the scoring engine and price history."""

    def get_realized_return(
        self,
        *,
        symbol: str,
        from_date: dt.date,
        horizon_days: int,
    ) -> Decimal | None: ...


@dataclass(frozen=True)
class StaticPriceProvider:
    """Deterministic provider backed by a flat dict."""

    table: dict[tuple[str, dt.date, int], Decimal]

    def get_realized_return(
        self,
        *,
        symbol: str,
        from_date: dt.date,
        horizon_days: int,
    ) -> Decimal | None:
        return self.table.get((symbol.upper(), from_date, horizon_days))


def _signed_excess(
    action: str,
    sym_return: Decimal,
    bench_return: Decimal,
) -> Decimal | None:
    """Return the directional excess return of one recommendation."""

    excess = sym_return - bench_return
    if action in _BUY_ACTIONS:
        return excess
    if action in _SELL_ACTIONS:
        return -excess
    if action in _NEUTRAL_ACTIONS:
        return Decimal("0")
    return None


def _score_id(
    *, agent_id: str, track_id: str, as_of_date: dt.date,
) -> str:
    body = f"{agent_id}\x00{track_id}\x00{as_of_date.isoformat()}"
    return "score_" + hashlib.sha256(body.encode("utf-8")).hexdigest()[:24]


def aggregate_rewards(rewards: Iterable[Decimal]) -> Decimal:
    items = list(rewards)
    if not items:
        return Decimal("0")
    total = sum(items, start=Decimal("0"))
    return (total / Decimal(len(items))).quantize(Decimal("0.000001"))


def score_recommendations_for_window(
    *,
    session: Session,
    run_id: str,
    as_of_date: dt.date,
    price_provider: PriceProvider,
    benchmark_symbol: str = _BENCHMARK_SYMBOL,
) -> list[tuple[str, str, Decimal, int]]:
    """Score every recommendation tied to ``run_id`` and persist rollups.

    Returns ``(agent_id, track_id, reward_score, observation_count)``
    tuples in the order they were written.
    """

    repo = Repository(session)
    recs = repo.list_recommendations(run_id=run_id)
    if not recs:
        return []

    per_rec_rewards: dict[tuple[str, str], list[Decimal]] = {}
    for rec in recs:
        bench_ret = price_provider.get_realized_return(
            symbol=benchmark_symbol,
            from_date=rec.created_at.date(),
            horizon_days=int(rec.time_horizon_days),
        )
        sym_ret = price_provider.get_realized_return(
            symbol=rec.symbol,
            from_date=rec.created_at.date(),
            horizon_days=int(rec.time_horizon_days),
        )
        if bench_ret is None or sym_ret is None:
            LOG.debug(
                "Skipping recommendation %s — missing price observation.",
                rec.recommendation_id,
            )
            continue
        reward = _signed_excess(rec.action, sym_ret, bench_ret)
        if reward is None:
            continue
        per_rec_rewards.setdefault((rec.agent_id, rec.track_id), []).append(
            reward,
        )

    out: list[tuple[str, str, Decimal, int]] = []
    for (agent_id, track_id), rewards in sorted(per_rec_rewards.items()):
        if not rewards:
            continue
        avg = aggregate_rewards(rewards)
        repo.upsert_agent_score(
            score_id=_score_id(
                agent_id=agent_id, track_id=track_id, as_of_date=as_of_date,
            ),
            agent_id=agent_id,
            track_id=track_id,
            as_of_date=as_of_date,
            reward_score=avg,
            observation_count=len(rewards),
            selected_return_pct=avg,
        )
        out.append((agent_id, track_id, avg, len(rewards)))
    return out


def compute_rewards_by_recommendation(
    *,
    session: Session,
    run_id: str,
    price_provider: PriceProvider,
    benchmark_symbol: str = _BENCHMARK_SYMBOL,
) -> dict[str, Decimal]:
    """Same evaluation as :func:`score_recommendations_for_window` but
    returns the per-``recommendation_id`` map without persisting.

    Used by :mod:`stockripper.scoring.judge_regret` so regret can run
    without re-pricing.
    """

    repo = Repository(session)
    out: dict[str, Decimal] = {}
    for rec in repo.list_recommendations(run_id=run_id):
        bench_ret = price_provider.get_realized_return(
            symbol=benchmark_symbol,
            from_date=rec.created_at.date(),
            horizon_days=int(rec.time_horizon_days),
        )
        sym_ret = price_provider.get_realized_return(
            symbol=rec.symbol,
            from_date=rec.created_at.date(),
            horizon_days=int(rec.time_horizon_days),
        )
        if bench_ret is None or sym_ret is None:
            continue
        reward = _signed_excess(rec.action, sym_ret, bench_ret)
        if reward is None:
            continue
        out[rec.recommendation_id] = reward
    return out


__all__ = (
    "PriceObservation",
    "PriceProvider",
    "StaticPriceProvider",
    "aggregate_rewards",
    "compute_rewards_by_recommendation",
    "score_recommendations_for_window",
)
