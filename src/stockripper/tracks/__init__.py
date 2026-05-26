"""Strategy-track registry and idempotent seeding.

The 8 MVP tracks from PROJECT_SPEC.md §5.3 live here as a single source of
truth that ``stockripper db seed`` (and tests) drives into the ledger.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal
from typing import Final

from sqlalchemy.orm import Session

from stockripper.db.repository import Repository
from stockripper.risk import DEFAULT_RISK_POLICIES, RiskPolicyParams


@dataclass(frozen=True)
class TrackSpec:
    """In-code declaration of a strategy track.

    Mirrors the columns in ``strategy_tracks`` plus a reference to the
    ``RiskPolicyParams`` value object used to populate ``risk_policies``.
    """

    track_id: str
    name: str
    philosophy: str
    risk_policy_id: str
    starting_equity_usd: Decimal
    enabled: bool = True

    @property
    def risk_policy(self) -> RiskPolicyParams:
        return DEFAULT_RISK_POLICIES[self.risk_policy_id]

    @property
    def judge_objective(self) -> str:
        return self.risk_policy.judge_objective


# All starting equities use 100k paper dollars by default so head-to-head
# comparison on a per-window basis is apples-to-apples; the user can change
# starting equity per-track via config later.
_DEFAULT_STARTING_EQUITY: Final[Decimal] = Decimal("100000.00")


DEFAULT_TRACKS: Final[tuple[TrackSpec, ...]] = (
    TrackSpec(
        track_id="conservative",
        name="Conservative",
        philosophy="Capital preservation, broad diversification, long-only.",
        risk_policy_id="rp_conservative",
        starting_equity_usd=_DEFAULT_STARTING_EQUITY,
    ),
    TrackSpec(
        track_id="balanced",
        name="Balanced",
        philosophy="Growth with sane risk; long with simple option hedges.",
        risk_policy_id="rp_balanced",
        starting_equity_usd=_DEFAULT_STARTING_EQUITY,
    ),
    TrackSpec(
        track_id="aggressive",
        name="Aggressive",
        philosophy="Growth-first; accepts drawdown; uses shorts and options spreads.",
        risk_policy_id="rp_aggressive",
        starting_equity_usd=_DEFAULT_STARTING_EQUITY,
    ),
    TrackSpec(
        track_id="concentrated",
        name="Concentrated",
        philosophy="High-conviction bets; few large positions.",
        risk_policy_id="rp_concentrated",
        starting_equity_usd=_DEFAULT_STARTING_EQUITY,
    ),
    TrackSpec(
        track_id="yolo",
        name="YOLO",
        philosophy="Maximum aggression; raw-return objective; everything Alpaca paper supports.",
        risk_policy_id="rp_yolo",
        starting_equity_usd=_DEFAULT_STARTING_EQUITY,
    ),
    TrackSpec(
        track_id="quant_signal",
        name="Quant Signal",
        philosophy="Rules-based long/short equities, no LLM judgment.",
        risk_policy_id="rp_quant_signal",
        starting_equity_usd=_DEFAULT_STARTING_EQUITY,
    ),
    TrackSpec(
        track_id="random_baseline",
        name="Random Baseline",
        philosophy="Random equal-weight picks from eligible long-only universe.",
        risk_policy_id="rp_random_baseline",
        starting_equity_usd=_DEFAULT_STARTING_EQUITY,
    ),
    TrackSpec(
        track_id="benchmark",
        name="Benchmark",
        philosophy="Hold SPY/QQQ/IWM/cash; rebalance monthly.",
        risk_policy_id="rp_benchmark",
        starting_equity_usd=_DEFAULT_STARTING_EQUITY,
    ),
)


def seed_default_tracks(session: Session) -> tuple[int, int]:
    """Idempotently seed risk policies and strategy tracks.

    Returns a ``(policies_written, tracks_written)`` tuple of how many rows
    were created or updated. Safe to call repeatedly; existing rows are
    refreshed in place.
    """

    repo = Repository(session)

    policies_written = 0
    for policy_id, params in DEFAULT_RISK_POLICIES.items():
        repo.upsert_risk_policy(
            risk_policy_id=policy_id,
            label=policy_id.removeprefix("rp_"),
            params=params.model_dump(mode="json"),
        )
        policies_written += 1

    tracks_written = 0
    for spec in DEFAULT_TRACKS:
        repo.upsert_strategy_track(
            track_id=spec.track_id,
            name=spec.name,
            philosophy=spec.philosophy,
            risk_policy_id=spec.risk_policy_id,
            judge_objective=spec.judge_objective,
            starting_equity_usd=spec.starting_equity_usd,
            enabled=spec.enabled,
        )
        tracks_written += 1

    return policies_written, tracks_written


__all__ = ("DEFAULT_TRACKS", "TrackSpec", "seed_default_tracks")
