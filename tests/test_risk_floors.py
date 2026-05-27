"""Tests for the universal floors (spec §16.1)."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

import pytest

from stockripper.agents.schemas import (
    ActionItem,
    ActionOrderType,
    OrderSide,
    RecommendationInstrument,
)
from stockripper.risk.floors import (
    FloorCode,
    FloorContext,
    FloorViolation,
    check_floors,
)


def _make_action(**overrides: object) -> ActionItem:
    defaults: dict[str, object] = {
        "action_id": "act_test",
        "track_id": "balanced",
        "symbol": "AAPL",
        "instrument": RecommendationInstrument.EQUITY,
        "side": OrderSide.BUY,
        "target_notional_usd": Decimal("1000"),
        "order_type": ActionOrderType.MARKET,
        "rationale": "test buy",
    }
    defaults.update(overrides)
    return ActionItem(**defaults)  # type: ignore[arg-type]


def _ctx(**overrides: object) -> FloorContext:
    defaults: dict[str, object] = {
        "kill_switch_engaged": False,
        "kill_reason": None,
        "track_paused": False,
        "pause_reason": None,
        "client_order_id": "coid_balanced_abc123",
        "has_audit_row": True,
    }
    defaults.update(overrides)
    return FloorContext(**defaults)  # type: ignore[arg-type]


def test_floor_blocks_when_kill_switch_engaged() -> None:
    with pytest.raises(FloorViolation) as exc_info:
        check_floors(
            action=_make_action(),
            context=_ctx(kill_switch_engaged=True, kill_reason="ops_drill"),
        )
    assert exc_info.value.code == FloorCode.KILL_SWITCH
    assert "ops_drill" in exc_info.value.message


def test_floor_blocks_when_track_paused() -> None:
    with pytest.raises(FloorViolation) as exc_info:
        check_floors(
            action=_make_action(),
            context=_ctx(track_paused=True, pause_reason="manual"),
        )
    assert exc_info.value.code == FloorCode.TRACK_PAUSED


def test_floor_blocks_when_client_order_id_missing() -> None:
    with pytest.raises(FloorViolation) as exc_info:
        check_floors(
            action=_make_action(),
            context=_ctx(client_order_id=None),
        )
    assert exc_info.value.code == FloorCode.IDEMPOTENCY


def test_floor_blocks_when_client_order_id_too_long() -> None:
    with pytest.raises(FloorViolation) as exc_info:
        check_floors(
            action=_make_action(),
            context=_ctx(client_order_id="x" * 49),
        )
    assert exc_info.value.code == FloorCode.IDEMPOTENCY


def test_floor_blocks_when_audit_row_missing() -> None:
    with pytest.raises(FloorViolation) as exc_info:
        check_floors(
            action=_make_action(),
            context=_ctx(has_audit_row=False),
        )
    assert exc_info.value.code == FloorCode.AUDIT_COMPLETENESS


def test_floor_passes_for_well_formed_action() -> None:
    # Should not raise.
    check_floors(action=_make_action(), context=_ctx())


def test_floor_blocks_when_paper_endpoint_invariant_violated() -> None:
    """The paper-endpoint floor delegates to ``StockripperSettings``.

    We exercise that delegation by passing a settings stub whose
    ``assert_paper_only`` raises :class:`PaperEndpointError`.
    """

    from stockripper.config import PaperEndpointError

    class _BadSettings:
        def assert_paper_only(self) -> None:
            raise PaperEndpointError("non-paper endpoint configured")

    with pytest.raises(FloorViolation) as exc_info:
        check_floors(
            action=_make_action(),
            context=_ctx(),
            settings=_BadSettings(),  # type: ignore[arg-type]
        )
    assert exc_info.value.code == FloorCode.PAPER_ENDPOINT


def test_floor_freezes_context() -> None:
    """FloorContext should be a frozen dataclass to avoid mid-check mutation."""

    from dataclasses import FrozenInstanceError

    ctx = _ctx()
    with pytest.raises(FrozenInstanceError):
        ctx.kill_switch_engaged = True  # type: ignore[misc]


def test_floor_violation_repr_includes_code_and_message() -> None:
    v = FloorViolation(FloorCode.KILL_SWITCH, "kill engaged")
    assert "floor_kill_switch" in repr(v)
    assert "kill engaged" in repr(v)


# Suppress the unused-import warning from pytest using the module.
_ = dt
