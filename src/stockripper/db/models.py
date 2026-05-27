"""SQLAlchemy 2.x ORM models for the StockRipper ledger.

Schema follows PROJECT_SPEC.md §15. Cross-database types are used so unit
tests can run on SQLite in-memory while production runs on PostgreSQL. The
``JSON`` type maps to ``JSONB`` on PostgreSQL automatically via the dialect.
"""

from __future__ import annotations

import datetime as dt
from decimal import Decimal
from typing import Any

from sqlalchemy import (
    JSON,
    Boolean,
    CheckConstraint,
    Date,
    DateTime,
    ForeignKey,
    Integer,
    Numeric,
    String,
    UniqueConstraint,
    func,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, relationship


class Base(DeclarativeBase):
    """Common declarative base for every ledger table."""


def _utcnow() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


class RiskPolicy(Base):
    __tablename__ = "risk_policies"

    risk_policy_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    label: Mapped[str] = mapped_column(String(128), nullable=False)
    params_json: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False)
    created_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
        server_default=func.now(),
    )

    tracks: Mapped[list[StrategyTrack]] = relationship(
        back_populates="risk_policy",
    )


class StrategyTrack(Base):
    __tablename__ = "strategy_tracks"

    track_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    name: Mapped[str] = mapped_column(String(128), nullable=False)
    philosophy: Mapped[str] = mapped_column(String(512), nullable=False)
    risk_policy_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("risk_policies.risk_policy_id"), nullable=False,
    )
    judge_objective: Mapped[str] = mapped_column(String(64), nullable=False)
    enabled: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    starting_equity_usd: Mapped[Decimal] = mapped_column(
        Numeric(18, 2), nullable=False,
    )
    created_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
        server_default=func.now(),
    )

    risk_policy: Mapped[RiskPolicy] = relationship(back_populates="tracks")
    snapshots: Mapped[list[TrackSnapshot]] = relationship(
        back_populates="track", cascade="all, delete-orphan",
    )
    orders: Mapped[list[Order]] = relationship(back_populates="track")


class Run(Base):
    __tablename__ = "runs"

    run_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    experiment_id: Mapped[str | None] = mapped_column(String(64), nullable=True)
    trading_day: Mapped[dt.date] = mapped_column(Date, nullable=False)
    window_label: Mapped[str] = mapped_column(String(64), nullable=False)
    status: Mapped[str] = mapped_column(String(32), nullable=False)
    started_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )
    completed_at: Mapped[dt.datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True,
    )
    config_hash: Mapped[str] = mapped_column(String(64), nullable=False)
    notes: Mapped[str | None] = mapped_column(String(2048), nullable=True)


class Recommendation(Base):
    __tablename__ = "recommendations"

    recommendation_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    run_id: Mapped[str | None] = mapped_column(
        String(64), ForeignKey("runs.run_id"), nullable=True,
    )
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    agent_id: Mapped[str] = mapped_column(String(64), nullable=False)
    symbol: Mapped[str] = mapped_column(String(32), nullable=False)
    instrument_type: Mapped[str] = mapped_column(String(32), nullable=False)
    action: Mapped[str] = mapped_column(String(32), nullable=False)
    conviction: Mapped[Decimal] = mapped_column(Numeric(6, 4), nullable=False)
    time_horizon_days: Mapped[int] = mapped_column(Integer, nullable=False)
    suggested_notional_usd: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 2), nullable=True,
    )
    suggested_pct_equity: Mapped[Decimal | None] = mapped_column(
        Numeric(6, 4), nullable=True,
    )
    expected_return_pct: Mapped[Decimal | None] = mapped_column(
        Numeric(8, 4), nullable=True,
    )
    max_expected_drawdown_pct: Mapped[Decimal | None] = mapped_column(
        Numeric(8, 4), nullable=True,
    )
    thesis: Mapped[str | None] = mapped_column(String(4096), nullable=True)
    raw_output_uri: Mapped[str | None] = mapped_column(String(512), nullable=True)
    schema_valid: Mapped[bool] = mapped_column(Boolean, nullable=False)
    created_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )


class JudgeDecision(Base):
    __tablename__ = "judge_decisions"

    decision_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    run_id: Mapped[str | None] = mapped_column(
        String(64), ForeignKey("runs.run_id"), nullable=True,
    )
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    judge_agent_id: Mapped[str] = mapped_column(String(64), nullable=False)
    portfolio_posture: Mapped[str | None] = mapped_column(String(64), nullable=True)
    raw_output_uri: Mapped[str | None] = mapped_column(String(512), nullable=True)
    created_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )

    actions: Mapped[list[DecisionAction]] = relationship(
        back_populates="decision", cascade="all, delete-orphan",
    )


class DecisionAction(Base):
    __tablename__ = "decision_actions"

    action_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    decision_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("judge_decisions.decision_id"), nullable=False,
    )
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    symbol: Mapped[str] = mapped_column(String(32), nullable=False)
    instrument_type: Mapped[str] = mapped_column(String(32), nullable=False)
    action: Mapped[str] = mapped_column(String(32), nullable=False)
    target_notional_usd: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 2), nullable=True,
    )
    target_pct_equity: Mapped[Decimal | None] = mapped_column(
        Numeric(6, 4), nullable=True,
    )
    order_type: Mapped[str | None] = mapped_column(String(32), nullable=True)
    limit_price: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 4), nullable=True,
    )
    stop_price: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 4), nullable=True,
    )
    time_in_force: Mapped[str | None] = mapped_column(String(8), nullable=True)
    leg_json: Mapped[dict[str, Any] | None] = mapped_column(JSON, nullable=True)
    risk_status: Mapped[str | None] = mapped_column(String(32), nullable=True)
    rationale: Mapped[str | None] = mapped_column(String(4096), nullable=True)

    decision: Mapped[JudgeDecision] = relationship(back_populates="actions")
    orders: Mapped[list[Order]] = relationship(back_populates="action")


class Order(Base):
    __tablename__ = "orders"
    __table_args__ = (
        UniqueConstraint("client_order_id", name="uq_orders_client_order_id"),
    )

    local_order_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    action_id: Mapped[str | None] = mapped_column(
        String(64), ForeignKey("decision_actions.action_id"), nullable=True,
    )
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    alpaca_order_id: Mapped[str | None] = mapped_column(String(64), nullable=True)
    client_order_id: Mapped[str] = mapped_column(String(64), nullable=False)
    symbol: Mapped[str] = mapped_column(String(32), nullable=False)
    side: Mapped[str] = mapped_column(String(16), nullable=False)
    order_type: Mapped[str] = mapped_column(String(32), nullable=False)
    time_in_force: Mapped[str] = mapped_column(String(8), nullable=False)
    requested_notional_usd: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 2), nullable=True,
    )
    requested_qty: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 6), nullable=True,
    )
    limit_price: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 4), nullable=True,
    )
    stop_price: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 4), nullable=True,
    )
    status: Mapped[str] = mapped_column(String(32), nullable=False)
    submitted_at: Mapped[dt.datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True,
    )
    raw_request_uri: Mapped[str | None] = mapped_column(String(512), nullable=True)
    raw_response_uri: Mapped[str | None] = mapped_column(String(512), nullable=True)

    track: Mapped[StrategyTrack] = relationship(back_populates="orders")
    action: Mapped[DecisionAction | None] = relationship(back_populates="orders")
    fills: Mapped[list[Fill]] = relationship(
        back_populates="order", cascade="all, delete-orphan",
    )


class Fill(Base):
    __tablename__ = "fills"

    fill_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    local_order_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("orders.local_order_id"), nullable=False,
    )
    filled_qty: Mapped[Decimal] = mapped_column(Numeric(18, 6), nullable=False)
    filled_avg_price: Mapped[Decimal] = mapped_column(
        Numeric(18, 4), nullable=False,
    )
    filled_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False,
    )

    order: Mapped[Order] = relationship(back_populates="fills")


class TrackSnapshot(Base):
    __tablename__ = "track_snapshots"

    snapshot_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    run_id: Mapped[str | None] = mapped_column(
        String(64), ForeignKey("runs.run_id"), nullable=True,
    )
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    captured_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False,
    )
    equity: Mapped[Decimal] = mapped_column(Numeric(18, 2), nullable=False)
    cash: Mapped[Decimal] = mapped_column(Numeric(18, 2), nullable=False)
    buying_power: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 2), nullable=True,
    )
    gross_exposure: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 2), nullable=True,
    )
    net_exposure: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 2), nullable=True,
    )
    short_exposure: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 2), nullable=True,
    )
    options_notional: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 2), nullable=True,
    )
    raw_snapshot_uri: Mapped[str | None] = mapped_column(String(512), nullable=True)

    track: Mapped[StrategyTrack] = relationship(back_populates="snapshots")


# ---------------------------------------------------------------------------
# Phase 4 — agent run audit + window/track lifecycle + control-plane state
# ---------------------------------------------------------------------------
class TrackRun(Base):
    """One track's execution within a window-level Run (Phase 4)."""

    __tablename__ = "track_runs"
    __table_args__ = (
        UniqueConstraint(
            "run_id", "track_id", "packet_id",
            name="uq_track_runs_run_track_packet",
        ),
    )

    track_run_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    run_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("runs.run_id"), nullable=False,
    )
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    packet_id: Mapped[str] = mapped_column(String(64), nullable=False)
    symbol: Mapped[str] = mapped_column(String(32), nullable=False)
    status: Mapped[str] = mapped_column(String(32), nullable=False)
    """One of: ok, skipped_paused, aborted_kill, failed, partial."""
    interrupt_reason: Mapped[str | None] = mapped_column(String(512), nullable=True)
    started_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False,
    )
    completed_at: Mapped[dt.datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True,
    )

    agent_runs: Mapped[list[AgentRun]] = relationship(
        back_populates="track_run", cascade="all, delete-orphan",
    )


class AgentRun(Base):
    """Audit record for one agent invocation within a TrackRun (Phase 4)."""

    __tablename__ = "agent_runs"

    agent_run_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    track_run_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("track_runs.track_run_id"), nullable=False,
    )
    run_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("runs.run_id"), nullable=False,
    )
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    agent_id: Mapped[str] = mapped_column(String(64), nullable=False)
    agent_version: Mapped[str] = mapped_column(String(32), nullable=False)
    output_schema_name: Mapped[str] = mapped_column(String(64), nullable=False)
    status: Mapped[str] = mapped_column(String(32), nullable=False)
    fingerprint_digest: Mapped[str] = mapped_column(String(64), nullable=False)
    model_id: Mapped[str] = mapped_column(String(128), nullable=False)
    seed: Mapped[int | None] = mapped_column(Integer, nullable=True)
    prompt_content_hash: Mapped[str] = mapped_column(String(64), nullable=False)
    schema_content_hash: Mapped[str] = mapped_column(String(64), nullable=False)
    input_content_hash: Mapped[str] = mapped_column(String(64), nullable=False)
    output_json: Mapped[dict[str, Any] | None] = mapped_column(JSON, nullable=True)
    raw_response_text: Mapped[str | None] = mapped_column(
        String(65535), nullable=True,
    )
    quarantine_reason: Mapped[str | None] = mapped_column(
        String(2048), nullable=True,
    )
    latency_ms: Mapped[int | None] = mapped_column(Integer, nullable=True)
    started_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False,
    )
    created_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )

    track_run: Mapped[TrackRun] = relationship(back_populates="agent_runs")


class KillSwitchState(Base):
    """Global kill-switch state. Singleton row enforced by ``id == 1``."""

    __tablename__ = "kill_switch_state"
    __table_args__ = (
        CheckConstraint("id = 1", name="ck_kill_switch_singleton"),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True, default=1)
    engaged: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    reason: Mapped[str | None] = mapped_column(String(512), nullable=True)
    engaged_at: Mapped[dt.datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True,
    )
    engaged_by: Mapped[str | None] = mapped_column(String(128), nullable=True)
    updated_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )


class TrackPauseState(Base):
    """Per-track pause state. One row per track that has ever been paused."""

    __tablename__ = "track_pause_state"

    track_id: Mapped[str] = mapped_column(
        String(64),
        ForeignKey("strategy_tracks.track_id"),
        primary_key=True,
    )
    paused: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    reason: Mapped[str | None] = mapped_column(String(512), nullable=True)
    paused_at: Mapped[dt.datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True,
    )
    paused_by: Mapped[str | None] = mapped_column(String(128), nullable=True)
    updated_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )


# ---------------------------------------------------------------------------
# Phase 6 — scoring tables (spec §15.2)
# ---------------------------------------------------------------------------
class AgentScore(Base):
    """One reward observation for an (agent, track, as_of_date) triple.

    Reward signal is derived per-recommendation from the realized symbol
    price move over the recommendation's nominal horizon vs the SPY
    benchmark over the same horizon. Multiple recommendations from the
    same agent on the same day collapse into one row keyed by
    ``(agent_id, track_id, as_of_date)`` so the dashboard can render a
    stable per-agent leaderboard.
    """

    __tablename__ = "agent_scores"
    __table_args__ = (
        UniqueConstraint(
            "agent_id", "track_id", "as_of_date",
            name="uq_agent_scores_agent_track_date",
        ),
    )

    score_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    agent_id: Mapped[str] = mapped_column(String(64), nullable=False)
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    as_of_date: Mapped[dt.date] = mapped_column(Date, nullable=False)
    reward_score: Mapped[Decimal] = mapped_column(Numeric(18, 6), nullable=False)
    calibration_score: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 6), nullable=True,
    )
    evidence_quality_score: Mapped[Decimal | None] = mapped_column(
        Numeric(18, 6), nullable=True,
    )
    shadow_return_pct: Mapped[Decimal | None] = mapped_column(
        Numeric(10, 6), nullable=True,
    )
    selected_return_pct: Mapped[Decimal | None] = mapped_column(
        Numeric(10, 6), nullable=True,
    )
    observation_count: Mapped[int] = mapped_column(Integer, nullable=False)
    created_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )


class TrackLeaderboardEntry(Base):
    """One row in the head-to-head track leaderboard for one window."""

    __tablename__ = "track_leaderboard"
    __table_args__ = (
        UniqueConstraint(
            "window_start", "window_end", "track_id",
            name="uq_track_leaderboard_window_track",
        ),
    )

    leaderboard_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    window_start: Mapped[dt.date] = mapped_column(Date, nullable=False)
    window_end: Mapped[dt.date] = mapped_column(Date, nullable=False)
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    cumulative_return_pct: Mapped[Decimal | None] = mapped_column(
        Numeric(10, 6), nullable=True,
    )
    sharpe: Mapped[Decimal | None] = mapped_column(Numeric(10, 6), nullable=True)
    sortino: Mapped[Decimal | None] = mapped_column(Numeric(10, 6), nullable=True)
    calmar: Mapped[Decimal | None] = mapped_column(Numeric(10, 6), nullable=True)
    max_drawdown_pct: Mapped[Decimal | None] = mapped_column(
        Numeric(10, 6), nullable=True,
    )
    win_rate: Mapped[Decimal | None] = mapped_column(
        Numeric(10, 6), nullable=True,
    )
    turnover: Mapped[Decimal | None] = mapped_column(
        Numeric(10, 6), nullable=True,
    )
    rank: Mapped[int | None] = mapped_column(Integer, nullable=True)
    created_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )


class JudgeRegretEntry(Base):
    """Per-(judge, track, as_of_date) regret record.

    Compares the judge's actual selected actions (and their realized
    reward) against the best-counterfactual judge over the same
    recommendation set. Drives the spec §8.3 / §19.4 judge leaderboard.
    """

    __tablename__ = "judge_regret"
    __table_args__ = (
        UniqueConstraint(
            "judge_agent_id", "track_id", "as_of_date",
            name="uq_judge_regret_judge_track_date",
        ),
    )

    regret_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    judge_agent_id: Mapped[str] = mapped_column(String(64), nullable=False)
    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("strategy_tracks.track_id"), nullable=False,
    )
    as_of_date: Mapped[dt.date] = mapped_column(Date, nullable=False)
    selected_reward: Mapped[Decimal] = mapped_column(Numeric(18, 6), nullable=False)
    best_alternative_reward: Mapped[Decimal] = mapped_column(
        Numeric(18, 6), nullable=False,
    )
    regret: Mapped[Decimal] = mapped_column(Numeric(18, 6), nullable=False)
    observation_count: Mapped[int] = mapped_column(Integer, nullable=False)
    created_at: Mapped[dt.datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utcnow,
    )
