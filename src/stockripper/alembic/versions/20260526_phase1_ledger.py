"""phase1 ledger initial

Revision ID: 20260526_phase1_ledger
Revises:
Create Date: 2026-05-26 00:00:00.000000

This migration creates the entire Phase 1 ledger schema (PROJECT_SPEC.md §15)
in a single step by delegating to ``Base.metadata.create_all``. We use this
shortcut intentionally for the first revision: every model is brand-new, so
there is no diff to capture, and ``create_all`` produces identical DDL on
SQLite (tests) and PostgreSQL (production) without per-dialect drift.

Subsequent migrations should use proper ``op.add_column``/``op.create_table``
operations so they can be reviewed and replayed deterministically.
"""

from __future__ import annotations

from collections.abc import Sequence

from alembic import op

from stockripper.db import Base

revision: str = "20260526_phase1_ledger"
down_revision: str | None | Sequence[str] = None
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    bind = op.get_bind()
    Base.metadata.create_all(bind=bind)


def downgrade() -> None:
    bind = op.get_bind()
    Base.metadata.drop_all(bind=bind)
