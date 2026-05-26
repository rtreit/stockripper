"""Typed repository layer over the SQLAlchemy session.

Each method takes a live :class:`Session` so callers control the transaction
boundary (typically via :func:`stockripper.db.engine.session_scope`). This
keeps the repository free of hidden session lifecycles and makes it trivially
testable.
"""

from __future__ import annotations

import datetime as dt
from decimal import Decimal
from typing import Any

from sqlalchemy import select
from sqlalchemy.orm import Session

from stockripper.db.models import (
    Fill,
    Order,
    RiskPolicy,
    StrategyTrack,
    TrackSnapshot,
)


class Repository:
    """Thin facade with the operations Phase-1 callers actually need."""

    def __init__(self, session: Session) -> None:
        self.session = session

    # ------------------------------------------------------------------
    # Risk policies
    # ------------------------------------------------------------------
    def upsert_risk_policy(
        self,
        risk_policy_id: str,
        label: str,
        params: dict[str, Any],
    ) -> RiskPolicy:
        existing = self.session.get(RiskPolicy, risk_policy_id)
        if existing is not None:
            existing.label = label
            existing.params_json = params
            return existing
        policy = RiskPolicy(
            risk_policy_id=risk_policy_id,
            label=label,
            params_json=params,
        )
        self.session.add(policy)
        return policy

    def get_risk_policy(self, risk_policy_id: str) -> RiskPolicy | None:
        return self.session.get(RiskPolicy, risk_policy_id)

    # ------------------------------------------------------------------
    # Strategy tracks
    # ------------------------------------------------------------------
    def upsert_strategy_track(
        self,
        *,
        track_id: str,
        name: str,
        philosophy: str,
        risk_policy_id: str,
        judge_objective: str,
        starting_equity_usd: Decimal,
        enabled: bool = True,
    ) -> StrategyTrack:
        existing = self.session.get(StrategyTrack, track_id)
        if existing is not None:
            existing.name = name
            existing.philosophy = philosophy
            existing.risk_policy_id = risk_policy_id
            existing.judge_objective = judge_objective
            existing.starting_equity_usd = starting_equity_usd
            existing.enabled = enabled
            return existing
        track = StrategyTrack(
            track_id=track_id,
            name=name,
            philosophy=philosophy,
            risk_policy_id=risk_policy_id,
            judge_objective=judge_objective,
            starting_equity_usd=starting_equity_usd,
            enabled=enabled,
        )
        self.session.add(track)
        return track

    def get_strategy_track(self, track_id: str) -> StrategyTrack | None:
        return self.session.get(StrategyTrack, track_id)

    def list_strategy_tracks(
        self, *, enabled_only: bool = False,
    ) -> list[StrategyTrack]:
        stmt = select(StrategyTrack).order_by(StrategyTrack.track_id)
        if enabled_only:
            stmt = stmt.where(StrategyTrack.enabled.is_(True))
        return list(self.session.execute(stmt).scalars())

    # ------------------------------------------------------------------
    # Orders + fills (Phase-1 reconciliation surface)
    # ------------------------------------------------------------------
    def upsert_order_from_alpaca(
        self,
        *,
        track_id: str,
        alpaca_order: dict[str, Any],
    ) -> Order:
        """Reconcile an Alpaca order payload into the local ``orders`` table.

        The Alpaca order's ``id`` becomes ``alpaca_order_id``; the local
        ``client_order_id`` is taken straight from the Alpaca payload so the
        deterministic ID generated at submission time round-trips. The
        ``local_order_id`` uses the same ``client_order_id`` for simplicity
        until we have a dedicated identity scheme.
        """

        client_order_id = str(alpaca_order["client_order_id"])
        existing = self.session.execute(
            select(Order).where(Order.client_order_id == client_order_id)
        ).scalar_one_or_none()

        submitted_at = _parse_dt(alpaca_order.get("submitted_at"))
        defaults: dict[str, Any] = {
            "track_id": track_id,
            "alpaca_order_id": str(alpaca_order["id"]),
            "client_order_id": client_order_id,
            "symbol": str(alpaca_order["symbol"]).upper(),
            "side": str(alpaca_order["side"]),
            "order_type": str(alpaca_order.get("order_type") or alpaca_order.get("type")),
            "time_in_force": str(alpaca_order["time_in_force"]),
            "requested_notional_usd": _to_decimal(alpaca_order.get("notional")),
            "requested_qty": _to_decimal(alpaca_order.get("qty")),
            "limit_price": _to_decimal(alpaca_order.get("limit_price")),
            "stop_price": _to_decimal(alpaca_order.get("stop_price")),
            "status": str(alpaca_order["status"]),
            "submitted_at": submitted_at,
        }

        if existing is None:
            order = Order(local_order_id=client_order_id, **defaults)
            self.session.add(order)
            return order

        for key, value in defaults.items():
            setattr(existing, key, value)
        return existing

    def record_fill(
        self,
        *,
        fill_id: str,
        local_order_id: str,
        filled_qty: Decimal,
        filled_avg_price: Decimal,
        filled_at: dt.datetime,
    ) -> Fill:
        existing = self.session.get(Fill, fill_id)
        if existing is not None:
            existing.filled_qty = filled_qty
            existing.filled_avg_price = filled_avg_price
            existing.filled_at = filled_at
            return existing
        fill = Fill(
            fill_id=fill_id,
            local_order_id=local_order_id,
            filled_qty=filled_qty,
            filled_avg_price=filled_avg_price,
            filled_at=filled_at,
        )
        self.session.add(fill)
        return fill

    # ------------------------------------------------------------------
    # Track snapshots (reconciliation output)
    # ------------------------------------------------------------------
    def record_track_snapshot(
        self,
        *,
        snapshot_id: str,
        track_id: str,
        captured_at: dt.datetime,
        equity: Decimal,
        cash: Decimal,
        buying_power: Decimal | None = None,
        gross_exposure: Decimal | None = None,
        net_exposure: Decimal | None = None,
        short_exposure: Decimal | None = None,
        options_notional: Decimal | None = None,
        run_id: str | None = None,
        raw_snapshot_uri: str | None = None,
    ) -> TrackSnapshot:
        existing = self.session.get(TrackSnapshot, snapshot_id)
        if existing is not None:
            existing.captured_at = captured_at
            existing.equity = equity
            existing.cash = cash
            existing.buying_power = buying_power
            existing.gross_exposure = gross_exposure
            existing.net_exposure = net_exposure
            existing.short_exposure = short_exposure
            existing.options_notional = options_notional
            existing.run_id = run_id
            existing.raw_snapshot_uri = raw_snapshot_uri
            return existing
        snap = TrackSnapshot(
            snapshot_id=snapshot_id,
            run_id=run_id,
            track_id=track_id,
            captured_at=captured_at,
            equity=equity,
            cash=cash,
            buying_power=buying_power,
            gross_exposure=gross_exposure,
            net_exposure=net_exposure,
            short_exposure=short_exposure,
            options_notional=options_notional,
            raw_snapshot_uri=raw_snapshot_uri,
        )
        self.session.add(snap)
        return snap

    def latest_track_snapshot(self, track_id: str) -> TrackSnapshot | None:
        stmt = (
            select(TrackSnapshot)
            .where(TrackSnapshot.track_id == track_id)
            .order_by(TrackSnapshot.captured_at.desc())
            .limit(1)
        )
        return self.session.execute(stmt).scalar_one_or_none()


# ----------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------
def _to_decimal(value: Any) -> Decimal | None:
    if value is None or value == "":
        return None
    if isinstance(value, Decimal):
        return value
    return Decimal(str(value))


def _parse_dt(value: Any) -> dt.datetime | None:
    if value is None:
        return None
    if isinstance(value, dt.datetime):
        return value if value.tzinfo else value.replace(tzinfo=dt.UTC)
    text = str(value).replace("Z", "+00:00")
    parsed = dt.datetime.fromisoformat(text)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=dt.UTC)
