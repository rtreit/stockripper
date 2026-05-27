"""Phase 6 scoring engine (spec §8, §15.2, §25 Phase 6).

Three responsibilities:

1. **Reward** — assign an interim reward score to every
   :class:`Recommendation` so the dashboard can render per-agent
   leaderboards within minutes of a window completing.
2. **Leaderboard** — compute head-to-head per-track summary stats
   (cumulative return, Sharpe, Sortino, Calmar, max drawdown, win
   rate, turnover) from ``track_snapshots`` rows.
3. **Judge regret** — for each (judge, track, date), compare the
   judge's chosen action against the best alternative the same
   council could have produced.

Everything in this package is pure logic over already-persisted ledger
rows; it never makes a network call. Reward/regret evaluation depends
on a :class:`PriceProvider` so live runs can plug in Alpaca while tests
use a deterministic stub.
"""

from __future__ import annotations

from stockripper.scoring.judge_regret import (
    JudgeRegretReport,
    compute_judge_regret_for_track,
    persist_judge_regret_for_track,
)
from stockripper.scoring.leaderboard import (
    LeaderboardMetrics,
    compute_leaderboard,
    persist_leaderboard,
)
from stockripper.scoring.reward import (
    PriceObservation,
    PriceProvider,
    StaticPriceProvider,
    aggregate_rewards,
    score_recommendations_for_window,
)

__all__ = (
    "JudgeRegretReport",
    "LeaderboardMetrics",
    "PriceObservation",
    "PriceProvider",
    "StaticPriceProvider",
    "aggregate_rewards",
    "compute_judge_regret_for_track",
    "compute_leaderboard",
    "persist_judge_regret_for_track",
    "persist_leaderboard",
    "score_recommendations_for_window",
)
