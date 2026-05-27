"""Lightweight per-track portfolio state used by the risk gate (Phase 5).

We deliberately keep this minimal for the MVP: just enough fields that the
gate's per-track caps (``max_position_pct_equity``,
``max_short_exposure_pct_equity``, etc.) have something concrete to evaluate
against. A full position-aware model lands in Phase 6+ when shadow
portfolios come online.
"""

from __future__ import annotations

import datetime as dt
from collections.abc import Iterable
from dataclasses import dataclass, field
from decimal import Decimal

from sqlalchemy.orm import Session

from stockripper.db.models import StrategyTrack
from stockripper.db.repository import Repository


@dataclass(frozen=True)
class Position:
    """One open position contributing to a track's exposure."""

    symbol: str
    qty: Decimal
    market_value: Decimal
    """Signed market value (negative for shorts)."""
    is_option: bool = False
    is_leveraged_etf: bool = False


@dataclass(frozen=True)
class PortfolioState:
    """Snapshot a risk gate evaluates an action against.

    All ``Decimal`` fields are in account currency (USD for Alpaca paper).
    ``equity`` is the headline number against which every per-track cap
    is expressed as a fraction.
    """

    track_id: str
    equity: Decimal
    cash: Decimal
    positions: tuple[Position, ...] = field(default_factory=tuple)
    captured_at: dt.datetime | None = None

    @property
    def gross_exposure(self) -> Decimal:
        return sum((abs(p.market_value) for p in self.positions), start=Decimal("0"))

    @property
    def net_exposure(self) -> Decimal:
        return sum((p.market_value for p in self.positions), start=Decimal("0"))

    @property
    def short_exposure(self) -> Decimal:
        return sum(
            (-p.market_value for p in self.positions if p.market_value < 0),
            start=Decimal("0"),
        )

    @property
    def options_notional(self) -> Decimal:
        return sum(
            (abs(p.market_value) for p in self.positions if p.is_option),
            start=Decimal("0"),
        )

    @property
    def leveraged_etf_notional(self) -> Decimal:
        return sum(
            (abs(p.market_value) for p in self.positions if p.is_leveraged_etf),
            start=Decimal("0"),
        )

    def position(self, symbol: str) -> Position | None:
        for p in self.positions:
            if p.symbol.upper() == symbol.upper():
                return p
        return None


def starting_equity_state(track: StrategyTrack) -> PortfolioState:
    """Construct a flat PortfolioState from a strategy track's starting equity.

    Useful for the very first window, before any snapshot has been recorded.
    """

    return PortfolioState(
        track_id=track.track_id,
        equity=track.starting_equity_usd,
        cash=track.starting_equity_usd,
        positions=(),
    )


def latest_state_from_snapshot(
    *,
    session: Session,
    track: StrategyTrack,
    positions: Iterable[Position] | None = None,
) -> PortfolioState:
    """Build a PortfolioState from the most recent ``track_snapshots`` row.

    Falls back to :func:`starting_equity_state` when no snapshot has been
    recorded yet. ``positions`` is an optional override for callers that
    already have a populated position list (e.g. the execution adapter
    threading post-reconciliation positions through).
    """

    repo = Repository(session)
    snap = repo.latest_track_snapshot(track.track_id)
    if snap is None:
        return starting_equity_state(track)
    return PortfolioState(
        track_id=track.track_id,
        equity=snap.equity,
        cash=snap.cash,
        positions=tuple(positions or ()),
        captured_at=snap.captured_at,
    )


__all__ = (
    "PortfolioState",
    "Position",
    "latest_state_from_snapshot",
    "starting_equity_state",
)
