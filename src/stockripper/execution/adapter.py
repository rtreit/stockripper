"""Phase-5 execution adapter (spec §16.1, §25 Phase 5).

The adapter is the **only** code path that may submit orders. The agent
graph never calls Alpaca directly — it produces :class:`ActionItem` rows;
the adapter is responsible for:

1. Re-checking the global kill switch + per-track pause (mid-window state
   can change after :func:`run_window` already started).
2. Running universal floors (:mod:`stockripper.risk.floors`).
3. Running the per-track risk gate (:mod:`stockripper.risk.gate`).
4. Computing the deterministic ``client_order_id`` from the canonical
   :class:`OrderIntent` so retries collapse to the same order.
5. Submitting via the underlying client (mock or Alpaca paper).
6. Persisting the ``orders`` row (and synthesizing a ``fills`` row in mock
   mode for fast end-to-end demos).

The submission contract is a small :class:`SubmissionResult` value object
so callers can render and persist outcomes without depending on Alpaca's
client surface.
"""

from __future__ import annotations

import datetime as dt
import hashlib
import logging
from collections.abc import Iterable
from dataclasses import dataclass, field
from decimal import Decimal
from enum import StrEnum
from typing import Any, Final, Protocol

from sqlalchemy.orm import sessionmaker
from sqlalchemy.orm.session import Session

from stockripper.agents.schemas import ActionItem, ActionOrderType, OrderSide
from stockripper.config import StockripperSettings
from stockripper.db.engine import session_scope
from stockripper.db.models import DecisionAction, StrategyTrack
from stockripper.db.repository import Repository
from stockripper.execution.client_order_id import (
    OrderIntent,
    build_client_order_id,
    build_intent_hash,
)
from stockripper.risk import DEFAULT_RISK_POLICIES, RiskPolicyParams
from stockripper.risk.floors import (
    FloorContext,
    FloorViolation,
    check_floors,
)
from stockripper.risk.gate import RiskDecision, RiskGate
from stockripper.risk.portfolio import (
    PortfolioState,
    latest_state_from_snapshot,
)

LOG: Final = logging.getLogger(__name__)


# --------------------------------------------------------------------------- #
# Result types
# --------------------------------------------------------------------------- #
class SubmissionStatus(StrEnum):
    """Outcome of a single :meth:`ExecutionAdapter.submit_action` call."""

    SUBMITTED = "submitted"
    """Order was newly submitted to the underlying broker."""

    DUPLICATE = "duplicate"
    """An order with the same ``client_order_id`` already existed (idempotent)."""

    REJECTED_FLOOR = "rejected_floor"
    """A universal floor blocked the submission."""

    REJECTED_RISK = "rejected_risk"
    """The per-track risk gate rejected the action."""


@dataclass(frozen=True)
class SubmissionResult:
    """What happened to one :class:`ActionItem`."""

    action_id: str
    track_id: str
    symbol: str
    status: SubmissionStatus
    client_order_id: str | None
    local_order_id: str | None
    reason: str | None = None
    risk_decision: RiskDecision | None = None
    risk_status_label: str = ""


# --------------------------------------------------------------------------- #
# Broker client protocol + mock + Alpaca implementations
# --------------------------------------------------------------------------- #
@dataclass(frozen=True)
class BrokerOrder:
    """Broker-side representation of a submitted order.

    Adapters return this from :meth:`BrokerClient.submit`. The execution
    adapter then persists it via :meth:`Repository.upsert_order`.
    """

    broker_order_id: str
    client_order_id: str
    status: str
    """Broker-reported status (e.g. ``new``, ``accepted``, ``filled``)."""

    submitted_at: dt.datetime
    filled_qty: Decimal | None = None
    filled_avg_price: Decimal | None = None
    filled_at: dt.datetime | None = None


class BrokerClient(Protocol):
    """Minimum surface the execution adapter needs from a broker client."""

    def submit(
        self,
        *,
        intent: OrderIntent,
        client_order_id: str,
        track_id: str,
        window_id: str,
    ) -> BrokerOrder: ...


# --------------------------------------------------------------------------- #
# Mock broker (default for tests + offline demos)
# --------------------------------------------------------------------------- #
@dataclass
class MockBrokerClient:
    """Always-fill mock broker.

    Synthesizes a ``filled`` :class:`BrokerOrder` immediately at either the
    intent's ``limit_price`` (when set) or a stable per-symbol pseudo price
    derived from a SHA-256 of the symbol. Useful for the Phase 5 acceptance
    smoke test where we want to see paper orders appear in the ledger
    without a live network call.
    """

    now: dt.datetime | None = None

    def submit(
        self,
        *,
        intent: OrderIntent,
        client_order_id: str,
        track_id: str,
        window_id: str,
    ) -> BrokerOrder:
        submitted_at = self.now if self.now is not None else _utcnow()
        price = intent.limit_price or _stable_mock_price(intent.symbol)
        qty: Decimal
        if intent.qty is not None:
            qty = intent.qty
        elif intent.notional is not None and price > 0:
            qty = (intent.notional / price).quantize(Decimal("0.000001"))
        else:
            qty = Decimal("0")
        return BrokerOrder(
            broker_order_id=f"mock_{hashlib.sha256(client_order_id.encode()).hexdigest()[:16]}",
            client_order_id=client_order_id,
            status="filled",
            submitted_at=submitted_at,
            filled_qty=qty,
            filled_avg_price=price,
            filled_at=submitted_at,
        )


# --------------------------------------------------------------------------- #
# Alpaca paper broker (production)
# --------------------------------------------------------------------------- #
class AlpacaPaperBrokerClient:
    """Adapter around ``alpaca.trading.client.TradingClient`` for paper trading.

    Construction asserts the paper endpoint via
    :meth:`StockripperSettings.assert_paper_only`. We deliberately do
    **not** export a factory that allows a non-paper URL — the MVP refuses
    to construct an execution-capable client against live endpoints
    (spec §16.1 universal floor #1, §16.1 #8 no real-money path).
    """

    def __init__(self, settings: StockripperSettings) -> None:
        settings.assert_paper_only()
        from alpaca.trading.client import TradingClient

        self._client = TradingClient(
            api_key=settings.alpaca_api_key_id.get_secret_value(),
            secret_key=settings.alpaca_api_secret_key.get_secret_value(),
            paper=True,
        )

    def submit(
        self,
        *,
        intent: OrderIntent,
        client_order_id: str,
        track_id: str,
        window_id: str,
    ) -> BrokerOrder:
        # Build alpaca-py request type lazily so unit tests don't need
        # alpaca-py installed unless they exercise this path.
        from alpaca.trading.enums import OrderSide as AlpacaSide
        from alpaca.trading.enums import TimeInForce as AlpacaTif
        from alpaca.trading.requests import (
            LimitOrderRequest,
            MarketOrderRequest,
        )

        side_map = {
            "buy": AlpacaSide.BUY,
            "sell": AlpacaSide.SELL,
            "sell_short": AlpacaSide.SELL,
            "buy_to_cover": AlpacaSide.BUY,
        }
        tif_map = {
            "day": AlpacaTif.DAY,
            "gtc": AlpacaTif.GTC,
            "ioc": AlpacaTif.IOC,
            "fok": AlpacaTif.FOK,
        }
        common: dict[str, Any] = {
            "symbol": intent.symbol,
            "side": side_map[intent.side],
            "time_in_force": tif_map[intent.time_in_force],
            "client_order_id": client_order_id,
        }
        if intent.qty is not None:
            common["qty"] = float(intent.qty)
        elif intent.notional is not None:
            common["notional"] = float(intent.notional)

        request: LimitOrderRequest | MarketOrderRequest
        if intent.order_type == "limit" and intent.limit_price is not None:
            request = LimitOrderRequest(limit_price=float(intent.limit_price), **common)
        else:
            request = MarketOrderRequest(**common)

        response: Any = self._client.submit_order(order_data=request)
        # alpaca-py returns a model object; map to BrokerOrder.
        return BrokerOrder(
            broker_order_id=str(response.id),
            client_order_id=str(response.client_order_id),
            status=str(response.status),
            submitted_at=_coerce_dt(response.submitted_at),
            filled_qty=_coerce_decimal(getattr(response, "filled_qty", None)),
            filled_avg_price=_coerce_decimal(getattr(response, "filled_avg_price", None)),
            filled_at=_coerce_dt(getattr(response, "filled_at", None)),
        )


# --------------------------------------------------------------------------- #
# Execution adapter
# --------------------------------------------------------------------------- #
@dataclass
class ExecutionAdapter:
    """Submits action items through the floors + risk-gate stack.

    Construction parameters:

    - ``session_factory``: sessionmaker used to open one short-lived
      session per :meth:`submit_action` call (control-plane re-check,
      idempotency lookup, order persistence, fill recording).
    - ``broker``: the :class:`BrokerClient` to forward approved orders to.
    - ``window_id``: window identifier passed into the
      :func:`build_client_order_id` derivation so the same intent inside
      a different window yields a different id.
    - ``settings``: :class:`StockripperSettings` (used by floors to
      re-assert the paper-endpoint invariant).
    - ``risk_policies``: optional override; defaults to
      :data:`stockripper.risk.DEFAULT_RISK_POLICIES`.
    - ``portfolio_provider``: optional callable yielding a
      :class:`PortfolioState`; defaults to a per-track snapshot lookup.
    """

    session_factory: sessionmaker[Session]
    broker: BrokerClient
    window_id: str
    settings: StockripperSettings | None = None
    risk_policies: dict[str, RiskPolicyParams] = field(
        default_factory=lambda: dict(DEFAULT_RISK_POLICIES),
    )
    now: dt.datetime | None = None

    def submit_action(self, action: ActionItem) -> SubmissionResult:
        intent = _action_to_intent(action)
        intent_hash = build_intent_hash(intent)
        client_order_id = build_client_order_id(
            track_id=action.track_id,
            intent_hash=intent_hash,
            window_id=self.window_id,
        )

        with session_scope(self.session_factory) as session:
            repo = Repository(session)

            track = repo.get_strategy_track(action.track_id)
            if track is None:
                # No track row → we cannot resolve policy or portfolio.
                # Treat as floor violation so this becomes audit-visible.
                _persist_action_status(
                    repo, action.action_id,
                    f"rejected_floor:unknown_track:{action.track_id}",
                )
                return SubmissionResult(
                    action_id=action.action_id,
                    track_id=action.track_id,
                    symbol=action.symbol,
                    status=SubmissionStatus.REJECTED_FLOOR,
                    client_order_id=None,
                    local_order_id=None,
                    reason=f"unknown_track:{action.track_id}",
                    risk_status_label=f"rejected_floor:unknown_track:{action.track_id}",
                )

            kill_state = repo.get_kill_switch()
            pause = repo.get_track_pause(action.track_id)
            # By contract, ``submit_action`` is invoked downstream of
            # ``persist_track_run`` (the audit row for this action was
            # already written). The audit-completeness floor is here so
            # an alternative caller cannot accidentally bypass it; when
            # called via :func:`run_window` the action's ``decision_actions``
            # row exists by construction.
            audit_row = repo.session.get(DecisionAction, action.action_id)
            ctx = FloorContext(
                kill_switch_engaged=kill_state.engaged,
                kill_reason=kill_state.reason,
                track_paused=bool(pause and pause.paused),
                pause_reason=(pause.reason if pause else None),
                client_order_id=client_order_id,
                has_audit_row=audit_row is not None,
            )

            try:
                check_floors(action=action, context=ctx, settings=self.settings)
            except FloorViolation as violation:
                label = f"rejected_floor:{violation.code.value}"
                _persist_action_status(repo, action.action_id, label)
                return SubmissionResult(
                    action_id=action.action_id,
                    track_id=action.track_id,
                    symbol=action.symbol,
                    status=SubmissionStatus.REJECTED_FLOOR,
                    client_order_id=client_order_id,
                    local_order_id=None,
                    reason=violation.message,
                    risk_status_label=label,
                )

            policy = self.risk_policies.get(track.risk_policy_id)
            if policy is None:
                label = f"rejected_floor:missing_policy:{track.risk_policy_id}"
                _persist_action_status(repo, action.action_id, label)
                return SubmissionResult(
                    action_id=action.action_id,
                    track_id=action.track_id,
                    symbol=action.symbol,
                    status=SubmissionStatus.REJECTED_FLOOR,
                    client_order_id=client_order_id,
                    local_order_id=None,
                    reason=f"missing risk policy {track.risk_policy_id!r}",
                    risk_status_label=label,
                )

            portfolio = self._build_portfolio_state(session, track)
            gate = RiskGate(policy=policy)
            decision = gate.evaluate(action=action, portfolio=portfolio)
            if decision.is_rejected:
                label = f"rejected_risk:{decision.summary().split(':', 1)[1]}"
                _persist_action_status(repo, action.action_id, label)
                return SubmissionResult(
                    action_id=action.action_id,
                    track_id=action.track_id,
                    symbol=action.symbol,
                    status=SubmissionStatus.REJECTED_RISK,
                    client_order_id=client_order_id,
                    local_order_id=None,
                    reason="; ".join(r.message for r in decision.rejections),
                    risk_decision=decision,
                    risk_status_label=label,
                )

            # Idempotency: if an order with this client_order_id already
            # exists we don't re-submit; we re-render the existing row.
            existing = repo.find_order_by_client_order_id(client_order_id)
            if existing is not None:
                _persist_action_status(repo, action.action_id, "approved")
                return SubmissionResult(
                    action_id=action.action_id,
                    track_id=action.track_id,
                    symbol=action.symbol,
                    status=SubmissionStatus.DUPLICATE,
                    client_order_id=client_order_id,
                    local_order_id=existing.local_order_id,
                    reason="client_order_id already submitted",
                    risk_decision=decision,
                    risk_status_label="approved",
                )

            broker_order = self.broker.submit(
                intent=intent,
                client_order_id=client_order_id,
                track_id=action.track_id,
                window_id=self.window_id,
            )

            order = repo.upsert_order(
                local_order_id=client_order_id,
                track_id=action.track_id,
                action_id=action.action_id,
                client_order_id=client_order_id,
                symbol=action.symbol,
                side=intent.side,
                order_type=intent.order_type,
                time_in_force=intent.time_in_force,
                status=broker_order.status,
                requested_notional_usd=intent.notional,
                requested_qty=intent.qty,
                limit_price=intent.limit_price,
                stop_price=intent.stop_price,
                alpaca_order_id=broker_order.broker_order_id,
                submitted_at=broker_order.submitted_at,
            )

            if (
                broker_order.filled_qty is not None
                and broker_order.filled_avg_price is not None
                and broker_order.filled_at is not None
            ):
                repo.record_fill(
                    fill_id=f"fill_{client_order_id}",
                    local_order_id=order.local_order_id,
                    filled_qty=broker_order.filled_qty,
                    filled_avg_price=broker_order.filled_avg_price,
                    filled_at=broker_order.filled_at,
                )

            _persist_action_status(repo, action.action_id, "approved")

            return SubmissionResult(
                action_id=action.action_id,
                track_id=action.track_id,
                symbol=action.symbol,
                status=SubmissionStatus.SUBMITTED,
                client_order_id=client_order_id,
                local_order_id=order.local_order_id,
                risk_decision=decision,
                risk_status_label="approved",
            )

    def submit_actions(
        self, actions: Iterable[ActionItem],
    ) -> tuple[SubmissionResult, ...]:
        return tuple(self.submit_action(a) for a in actions)

    def _build_portfolio_state(
        self, session: Session, track: StrategyTrack,
    ) -> PortfolioState:
        return latest_state_from_snapshot(session=session, track=track)


# --------------------------------------------------------------------------- #
# Helpers
# --------------------------------------------------------------------------- #
_ACTION_SIDE_TO_INTENT: Final[dict[OrderSide, str]] = {
    OrderSide.BUY: "buy",
    OrderSide.SELL: "sell",
    OrderSide.SELL_SHORT: "sell_short",
    OrderSide.BUY_TO_COVER: "buy_to_cover",
    OrderSide.MULTI_LEG: "multi_leg",
}

_ACTION_ORDER_TYPE_TO_INTENT: Final[dict[ActionOrderType, str]] = {
    ActionOrderType.MARKET: "market",
    ActionOrderType.LIMIT: "limit",
    ActionOrderType.STOP: "stop",
    ActionOrderType.STOP_LIMIT: "stop_limit",
}


def _action_to_intent(action: ActionItem) -> OrderIntent:
    """Build a canonical :class:`OrderIntent` from an :class:`ActionItem`.

    Multi-leg actions are out of scope for the Phase 5 MVP: we capture the
    leg shape in :attr:`OrderIntent.legs` so the deterministic
    ``client_order_id`` differs between leg structures, but the mock
    broker treats them as a single notional ticket.
    """

    legs: tuple[tuple[str, str], ...] | None = None
    if action.multi_leg is not None:
        legs = tuple(
            (leg.occ_symbol, leg.side.value) for leg in action.multi_leg.legs
        )
    return OrderIntent(
        symbol=action.symbol,
        side=_ACTION_SIDE_TO_INTENT[action.side],
        order_type=_ACTION_ORDER_TYPE_TO_INTENT[action.order_type],
        time_in_force=action.time_in_force,
        qty=None,
        notional=action.target_notional_usd,
        limit_price=action.limit_price,
        stop_price=action.stop_price,
        legs=legs,
    )


def _persist_action_status(
    repo: Repository, action_id: str, risk_status: str,
) -> None:
    """Best-effort risk_status persistence; missing rows are tolerated."""

    repo.set_action_risk_status(action_id=action_id, risk_status=risk_status)


def _stable_mock_price(symbol: str) -> Decimal:
    """Deterministic per-symbol pseudo price in [10, 1000].

    Lets the mock broker fill orders for any symbol without a live data
    feed. The price is stable across runs so deterministic-ID tests stay
    deterministic.
    """

    digest = hashlib.sha256(symbol.upper().encode("utf-8")).digest()
    raw = int.from_bytes(digest[:4], "big")
    # 10 .. 1010 USD
    return Decimal(10 + raw % 1000) + Decimal("0.50")


def _utcnow() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def _coerce_dt(value: object) -> dt.datetime:
    if isinstance(value, dt.datetime):
        return value if value.tzinfo else value.replace(tzinfo=dt.UTC)
    if value is None:
        return _utcnow()
    return _utcnow()


def _coerce_decimal(value: object) -> Decimal | None:
    if value is None:
        return None
    if isinstance(value, Decimal):
        return value
    try:
        return Decimal(str(value))
    except (TypeError, ValueError):
        return None


__all__ = (
    "AlpacaPaperBrokerClient",
    "BrokerClient",
    "BrokerOrder",
    "ExecutionAdapter",
    "MockBrokerClient",
    "SubmissionResult",
    "SubmissionStatus",
)
