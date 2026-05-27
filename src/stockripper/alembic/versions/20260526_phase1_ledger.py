"""phase1 ledger initial

Revision ID: 20260526_phase1_ledger
Revises:
Create Date: 2026-05-26 00:00:00.000000

This migration creates only the Phase-1 ledger tables (PROJECT_SPEC.md §15)
by passing an explicit allow-list of tables to ``Base.metadata.create_all``.
Later phases create their own tables via dedicated migrations so the schema
history stays linear and replayable.
"""

from __future__ import annotations

from collections.abc import Sequence

from alembic import op

from stockripper.db import Base

revision: str = "20260526_phase1_ledger"
down_revision: str | None | Sequence[str] = None
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

# Tables created in this revision. Add-only — never edit this list once
# the migration ships; new tables get their own migration.
_PHASE1_TABLES: tuple[str, ...] = (
    "risk_policies",
    "strategy_tracks",
    "runs",
    "recommendations",
    "judge_decisions",
    "decision_actions",
    "orders",
    "fills",
    "track_snapshots",
)


def upgrade() -> None:
    bind = op.get_bind()
    tables = [Base.metadata.tables[name] for name in _PHASE1_TABLES]
    Base.metadata.create_all(bind=bind, tables=tables)


def downgrade() -> None:
    bind = op.get_bind()
    tables = [Base.metadata.tables[name] for name in _PHASE1_TABLES]
    Base.metadata.drop_all(bind=bind, tables=tables)
