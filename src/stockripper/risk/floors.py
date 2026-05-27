"""Universal floors (spec §16.1).

These are the only hard limits that cannot be loosened by any track config
or any judge. They run **before** the per-track risk gate and **before**
the execution adapter ever touches an external endpoint.

Failure modes:

- :class:`FloorViolation` raised with a structured ``code`` so the caller
  (typically the execution adapter) can persist a precise rejection
  rationale on the ``decision_actions.risk_status`` column.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import StrEnum
from typing import Final

from stockripper.agents.schemas import ActionItem
from stockripper.config import PaperEndpointError, StockripperSettings


class FloorCode(StrEnum):
    """Structured reason codes for universal-floor rejections."""

    PAPER_ENDPOINT = "floor_paper_endpoint"
    IDEMPOTENCY = "floor_idempotency"
    SCHEMA_VALID = "floor_schema_valid"
    NO_LLM_DIRECT = "floor_no_llm_direct"
    AUDIT_COMPLETENESS = "floor_audit_completeness"
    KILL_SWITCH = "floor_kill_switch"
    TRACK_PAUSED = "floor_track_paused"


class FloorViolation(RuntimeError):  # noqa: N818 - "Violation" reads better than "Error" for a floor
    """One of the universal floors blocked an action."""

    def __init__(self, code: FloorCode, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message

    def __repr__(self) -> str:
        return f"FloorViolation(code={self.code.value!r}, message={self.message!r})"


@dataclass(frozen=True)
class FloorContext:
    """Inputs the floors check needs that are not on the action itself."""

    kill_switch_engaged: bool
    kill_reason: str | None
    track_paused: bool
    pause_reason: str | None
    client_order_id: str | None
    has_audit_row: bool


# ``client_order_id`` may be at most 48 characters per Alpaca docs.
_CLIENT_ORDER_ID_MAX_LEN: Final[int] = 48


def check_floors(
    *,
    action: ActionItem,
    context: FloorContext,
    settings: StockripperSettings | None = None,
) -> None:
    """Run every universal floor against an action.

    Raises :class:`FloorViolation` on the first failure. Order matters:
    kill switch and pause are cheapest and should short-circuit before
    we touch any other invariant.
    """

    if context.kill_switch_engaged:
        raise FloorViolation(
            FloorCode.KILL_SWITCH,
            f"kill switch engaged: {context.kill_reason or 'unknown'}",
        )

    if context.track_paused:
        raise FloorViolation(
            FloorCode.TRACK_PAUSED,
            f"track {action.track_id!r} is paused: {context.pause_reason or 'unknown'}",
        )

    if settings is not None:
        try:
            settings.assert_paper_only()
        except PaperEndpointError as exc:
            raise FloorViolation(FloorCode.PAPER_ENDPOINT, str(exc)) from exc

    # Schema-validity floor: ActionItem is a frozen pydantic model with
    # field validators, so by the time we receive one it has already
    # passed schema validation. We re-assert key invariants here so a
    # caller bypassing the schema cannot slip a malformed action in.
    if not action.symbol or not action.symbol.strip():
        raise FloorViolation(
            FloorCode.SCHEMA_VALID,
            f"action {action.action_id!r} has empty symbol",
        )
    if action.target_notional_usd is None and action.target_pct_equity is None:
        raise FloorViolation(
            FloorCode.SCHEMA_VALID,
            f"action {action.action_id!r} has neither target_notional_usd "
            "nor target_pct_equity",
        )
    if action.target_notional_usd is not None and action.target_pct_equity is not None:
        raise FloorViolation(
            FloorCode.SCHEMA_VALID,
            f"action {action.action_id!r} sets both target_notional_usd "
            "and target_pct_equity",
        )

    if context.client_order_id is None or not context.client_order_id.strip():
        raise FloorViolation(
            FloorCode.IDEMPOTENCY,
            f"action {action.action_id!r} has no deterministic client_order_id",
        )
    if len(context.client_order_id) > _CLIENT_ORDER_ID_MAX_LEN:
        raise FloorViolation(
            FloorCode.IDEMPOTENCY,
            f"client_order_id {context.client_order_id!r} exceeds "
            f"{_CLIENT_ORDER_ID_MAX_LEN}-char Alpaca limit",
        )

    if not context.has_audit_row:
        raise FloorViolation(
            FloorCode.AUDIT_COMPLETENESS,
            f"action {action.action_id!r} has no ledger row; refusing to submit",
        )


__all__ = (
    "FloorCode",
    "FloorContext",
    "FloorViolation",
    "check_floors",
)
