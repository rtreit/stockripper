"""Tests for the deterministic ``client_order_id`` generator."""

from __future__ import annotations

import re
from decimal import Decimal

import pytest

from stockripper.execution.client_order_id import (
    ALPACA_CLIENT_ORDER_ID_MAX,
    OrderIntent,
    build_client_order_id,
    build_intent_hash,
)

_ID_RE = re.compile(r"^[A-Za-z0-9_-]+$")


def _intent(**overrides: object) -> OrderIntent:
    defaults: dict[str, object] = {
        "symbol": "AAPL",
        "side": "buy",
        "order_type": "market",
        "time_in_force": "day",
        "qty": Decimal("10"),
    }
    defaults.update(overrides)
    return OrderIntent(**defaults)  # type: ignore[arg-type]


def test_identical_intent_hashes_match() -> None:
    a = _intent()
    b = _intent()
    assert build_intent_hash(a) == build_intent_hash(b)


def test_decimal_normalization_is_hash_stable() -> None:
    a = _intent(qty=Decimal("10.0"))
    b = _intent(qty=Decimal("10.00"))
    c = _intent(qty=Decimal("10"))
    assert build_intent_hash(a) == build_intent_hash(b) == build_intent_hash(c)


def test_different_symbol_changes_hash() -> None:
    assert build_intent_hash(_intent(symbol="AAPL")) != build_intent_hash(
        _intent(symbol="TSLA")
    )


def test_exactly_one_of_qty_or_notional_required() -> None:
    with pytest.raises(ValueError, match="exactly one of qty or notional"):
        OrderIntent(
            symbol="AAPL",
            side="buy",
            order_type="market",
            time_in_force="day",
        )
    with pytest.raises(ValueError, match="exactly one of qty or notional"):
        OrderIntent(
            symbol="AAPL",
            side="buy",
            order_type="market",
            time_in_force="day",
            qty=Decimal("10"),
            notional=Decimal("100"),
        )


def test_client_order_id_is_deterministic() -> None:
    intent_hash = build_intent_hash(_intent())
    a = build_client_order_id(
        track_id="conservative", intent_hash=intent_hash, window_id="2026-05-26-open",
    )
    b = build_client_order_id(
        track_id="conservative", intent_hash=intent_hash, window_id="2026-05-26-open",
    )
    assert a == b


def test_client_order_id_length_and_charset() -> None:
    intent_hash = build_intent_hash(_intent())
    coid = build_client_order_id(
        track_id="aggressive", intent_hash=intent_hash, window_id="2026-05-26-open",
    )
    assert len(coid) == 36
    assert len(coid) <= ALPACA_CLIENT_ORDER_ID_MAX
    assert _ID_RE.match(coid), coid


def test_track_id_changes_client_order_id() -> None:
    intent_hash = build_intent_hash(_intent())
    a = build_client_order_id(
        track_id="conservative", intent_hash=intent_hash, window_id="2026-05-26-open",
    )
    b = build_client_order_id(
        track_id="aggressive", intent_hash=intent_hash, window_id="2026-05-26-open",
    )
    assert a != b
    assert a.split("_", 1)[0] != b.split("_", 1)[0]


def test_window_id_changes_client_order_id() -> None:
    intent_hash = build_intent_hash(_intent())
    a = build_client_order_id(
        track_id="balanced", intent_hash=intent_hash, window_id="2026-05-26-open",
    )
    b = build_client_order_id(
        track_id="balanced", intent_hash=intent_hash, window_id="2026-05-27-open",
    )
    assert a != b


def test_short_track_id_still_yields_4_char_prefix() -> None:
    intent_hash = build_intent_hash(_intent())
    coid = build_client_order_id(
        track_id="a", intent_hash=intent_hash, window_id="w",
    )
    prefix, _ = coid.split("_", 1)
    assert len(prefix) == 4


def test_blank_inputs_rejected() -> None:
    h = build_intent_hash(_intent())
    with pytest.raises(ValueError):
        build_client_order_id(track_id="", intent_hash=h, window_id="w")
    with pytest.raises(ValueError):
        build_client_order_id(track_id="x", intent_hash="", window_id="w")
    with pytest.raises(ValueError):
        build_client_order_id(track_id="x", intent_hash=h, window_id="")
