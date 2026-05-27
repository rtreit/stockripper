"""phase 6 scoring tables

Revision ID: 20260530_phase6_scoring
Revises: 20260528_phase4_orchestrator
Create Date: 2026-05-30 00:00:00.000000

Adds the three tables Phase-6 scoring needs:

* ``agent_scores`` — per-(agent, track, date) reward / calibration / shadow.
* ``track_leaderboard`` — head-to-head per-window track ranking.
* ``judge_regret`` — per-judge regret summary per (track, date).
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "20260530_phase6_scoring"
down_revision: str | None | Sequence[str] = "20260528_phase4_orchestrator"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "agent_scores",
        sa.Column("score_id", sa.String(64), primary_key=True),
        sa.Column("agent_id", sa.String(64), nullable=False),
        sa.Column(
            "track_id", sa.String(64),
            sa.ForeignKey("strategy_tracks.track_id"), nullable=False,
        ),
        sa.Column("as_of_date", sa.Date(), nullable=False),
        sa.Column("reward_score", sa.Numeric(18, 6), nullable=False),
        sa.Column("calibration_score", sa.Numeric(18, 6), nullable=True),
        sa.Column("evidence_quality_score", sa.Numeric(18, 6), nullable=True),
        sa.Column("shadow_return_pct", sa.Numeric(10, 6), nullable=True),
        sa.Column("selected_return_pct", sa.Numeric(10, 6), nullable=True),
        sa.Column("observation_count", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.UniqueConstraint(
            "agent_id", "track_id", "as_of_date",
            name="uq_agent_scores_agent_track_date",
        ),
    )

    op.create_table(
        "track_leaderboard",
        sa.Column("leaderboard_id", sa.String(64), primary_key=True),
        sa.Column("window_start", sa.Date(), nullable=False),
        sa.Column("window_end", sa.Date(), nullable=False),
        sa.Column(
            "track_id", sa.String(64),
            sa.ForeignKey("strategy_tracks.track_id"), nullable=False,
        ),
        sa.Column("cumulative_return_pct", sa.Numeric(10, 6), nullable=True),
        sa.Column("sharpe", sa.Numeric(10, 6), nullable=True),
        sa.Column("sortino", sa.Numeric(10, 6), nullable=True),
        sa.Column("calmar", sa.Numeric(10, 6), nullable=True),
        sa.Column("max_drawdown_pct", sa.Numeric(10, 6), nullable=True),
        sa.Column("win_rate", sa.Numeric(10, 6), nullable=True),
        sa.Column("turnover", sa.Numeric(10, 6), nullable=True),
        sa.Column("rank", sa.Integer(), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.UniqueConstraint(
            "window_start", "window_end", "track_id",
            name="uq_track_leaderboard_window_track",
        ),
    )

    op.create_table(
        "judge_regret",
        sa.Column("regret_id", sa.String(64), primary_key=True),
        sa.Column("judge_agent_id", sa.String(64), nullable=False),
        sa.Column(
            "track_id", sa.String(64),
            sa.ForeignKey("strategy_tracks.track_id"), nullable=False,
        ),
        sa.Column("as_of_date", sa.Date(), nullable=False),
        sa.Column("selected_reward", sa.Numeric(18, 6), nullable=False),
        sa.Column("best_alternative_reward", sa.Numeric(18, 6), nullable=False),
        sa.Column("regret", sa.Numeric(18, 6), nullable=False),
        sa.Column("observation_count", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.UniqueConstraint(
            "judge_agent_id", "track_id", "as_of_date",
            name="uq_judge_regret_judge_track_date",
        ),
    )


def downgrade() -> None:
    op.drop_table("judge_regret")
    op.drop_table("track_leaderboard")
    op.drop_table("agent_scores")
