"""phase 4 orchestrator tables

Revision ID: 20260528_phase4_orchestrator
Revises: 20260526_phase1_ledger
Create Date: 2026-05-28 00:00:00.000000

Adds the four tables Phase-4 multi-track orchestration needs:

* ``track_runs`` — one row per (window, track, packet) execution.
* ``agent_runs`` — one row per agent invocation; uniform audit envelope
  for replay and per-agent attribution.
* ``kill_switch_state`` — singleton row holding the global kill flag.
* ``track_pause_state`` — per-track pause flag.
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "20260528_phase4_orchestrator"
down_revision: str | None | Sequence[str] = "20260526_phase1_ledger"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "track_runs",
        sa.Column("track_run_id", sa.String(64), primary_key=True),
        sa.Column(
            "run_id", sa.String(64),
            sa.ForeignKey("runs.run_id"), nullable=False,
        ),
        sa.Column(
            "track_id", sa.String(64),
            sa.ForeignKey("strategy_tracks.track_id"), nullable=False,
        ),
        sa.Column("packet_id", sa.String(64), nullable=False),
        sa.Column("symbol", sa.String(32), nullable=False),
        sa.Column("status", sa.String(32), nullable=False),
        sa.Column("interrupt_reason", sa.String(512), nullable=True),
        sa.Column("started_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("completed_at", sa.DateTime(timezone=True), nullable=True),
        sa.UniqueConstraint(
            "run_id", "track_id", "packet_id",
            name="uq_track_runs_run_track_packet",
        ),
    )

    op.create_table(
        "agent_runs",
        sa.Column("agent_run_id", sa.String(64), primary_key=True),
        sa.Column(
            "track_run_id", sa.String(64),
            sa.ForeignKey("track_runs.track_run_id"), nullable=False,
        ),
        sa.Column(
            "run_id", sa.String(64),
            sa.ForeignKey("runs.run_id"), nullable=False,
        ),
        sa.Column(
            "track_id", sa.String(64),
            sa.ForeignKey("strategy_tracks.track_id"), nullable=False,
        ),
        sa.Column("agent_id", sa.String(64), nullable=False),
        sa.Column("agent_version", sa.String(32), nullable=False),
        sa.Column("output_schema_name", sa.String(64), nullable=False),
        sa.Column("status", sa.String(32), nullable=False),
        sa.Column("fingerprint_digest", sa.String(64), nullable=False),
        sa.Column("model_id", sa.String(128), nullable=False),
        sa.Column("seed", sa.Integer(), nullable=True),
        sa.Column("prompt_content_hash", sa.String(64), nullable=False),
        sa.Column("schema_content_hash", sa.String(64), nullable=False),
        sa.Column("input_content_hash", sa.String(64), nullable=False),
        sa.Column("output_json", sa.JSON(), nullable=True),
        sa.Column("raw_response_text", sa.String(65535), nullable=True),
        sa.Column("quarantine_reason", sa.String(2048), nullable=True),
        sa.Column("latency_ms", sa.Integer(), nullable=True),
        sa.Column("started_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
    )

    op.create_table(
        "kill_switch_state",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column(
            "engaged", sa.Boolean(),
            nullable=False, server_default=sa.text("0"),
        ),
        sa.Column("reason", sa.String(512), nullable=True),
        sa.Column("engaged_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("engaged_by", sa.String(128), nullable=True),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
        sa.CheckConstraint("id = 1", name="ck_kill_switch_singleton"),
    )

    op.create_table(
        "track_pause_state",
        sa.Column(
            "track_id", sa.String(64),
            sa.ForeignKey("strategy_tracks.track_id"), primary_key=True,
        ),
        sa.Column(
            "paused", sa.Boolean(),
            nullable=False, server_default=sa.text("0"),
        ),
        sa.Column("reason", sa.String(512), nullable=True),
        sa.Column("paused_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("paused_by", sa.String(128), nullable=True),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
    )


def downgrade() -> None:
    op.drop_table("track_pause_state")
    op.drop_table("kill_switch_state")
    op.drop_table("agent_runs")
    op.drop_table("track_runs")
