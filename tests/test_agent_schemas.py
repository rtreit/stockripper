"""Schema-level validation tests for the Phase-3 agent contracts."""

from __future__ import annotations

import datetime as dt
import uuid
from decimal import Decimal

import pytest
from pydantic import ValidationError

from stockripper.agents.schemas import (
    ActionItem,
    ActionOrderType,
    AgentRecommendation,
    Evidence,
    EvidenceSourceType,
    MultiLegSpec,
    OptionLeg,
    OptionLegSide,
    OptionRight,
    OrderSide,
    PairLeg,
    RecommendationAction,
    RecommendationInstrument,
    recommendation_to_ledger_row,
    rule_based_fingerprint,
)


def _now() -> dt.datetime:
    return dt.datetime(2026, 7, 1, 14, 0, tzinfo=dt.UTC)


def _ev() -> Evidence:
    return Evidence.of_claim(
        claim="Margins are durable",
        source_type=EvidenceSourceType.COMPANY_FUNDAMENTALS,
        source_url="https://example.test/x",
        retrieved_at=_now(),
        confidence=0.7,
    )


def _recommendation(**overrides: object) -> AgentRecommendation:
    base: dict[str, object] = dict(
        recommendation_id=f"rec_{uuid.uuid4().hex[:16]}",
        agent_id="quality",
        agent_version="1.0.0",
        track_id="balanced",
        symbol="AAPL",
        instrument=RecommendationInstrument.EQUITY,
        action=RecommendationAction.BUY,
        conviction=Decimal("0.6"),
        time_horizon_days=180,
        suggested_notional_usd=Decimal("5000"),
        thesis="Compounder.",
        evidence=(_ev(),),
        created_at=_now(),
    )
    base.update(overrides)
    return AgentRecommendation(**base)  # type: ignore[arg-type]


def test_sizing_xor_rejects_both_notional_and_pct() -> None:
    with pytest.raises(ValidationError):
        _recommendation(
            suggested_notional_usd=Decimal("1000"),
            suggested_sizing_pct_of_equity=Decimal("0.1"),
        )


def test_sizing_xor_rejects_neither_for_trades() -> None:
    with pytest.raises(ValidationError):
        _recommendation(suggested_notional_usd=None)


def test_hold_does_not_require_sizing_or_evidence() -> None:
    rec = _recommendation(
        action=RecommendationAction.HOLD,
        suggested_notional_usd=None,
        evidence=(),
    )
    assert rec.action == RecommendationAction.HOLD


def test_avoid_does_not_require_sizing_or_evidence() -> None:
    rec = _recommendation(
        action=RecommendationAction.AVOID,
        suggested_notional_usd=None,
        evidence=(),
    )
    assert rec.action == RecommendationAction.AVOID


def test_trading_action_requires_evidence() -> None:
    with pytest.raises(ValidationError):
        _recommendation(evidence=())


def test_multi_leg_instrument_requires_multi_leg() -> None:
    with pytest.raises(ValidationError):
        _recommendation(
            instrument=RecommendationInstrument.MULTI_LEG_OPTION,
            action=RecommendationAction.MULTI_LEG,
        )


def _vertical_call_spread() -> MultiLegSpec:
    return MultiLegSpec(
        label="long_call_vertical",
        legs=(
            OptionLeg(
                underlying_symbol="AAPL",
                occ_symbol="AAPL260918C00200000",
                right=OptionRight.CALL,
                strike=Decimal("200"),
                expiration_date=dt.date(2026, 9, 18),
                side=OptionLegSide.BUY_TO_OPEN,
                ratio=1,
            ),
            OptionLeg(
                underlying_symbol="AAPL",
                occ_symbol="AAPL260918C00210000",
                right=OptionRight.CALL,
                strike=Decimal("210"),
                expiration_date=dt.date(2026, 9, 18),
                side=OptionLegSide.SELL_TO_OPEN,
                ratio=1,
            ),
        ),
    )


def test_multi_leg_payload_outside_multi_leg_instrument_rejected() -> None:
    spec = _vertical_call_spread()
    with pytest.raises(ValidationError):
        _recommendation(multi_leg=spec)


def test_multi_leg_happy_path() -> None:
    spec = _vertical_call_spread()
    rec = _recommendation(
        instrument=RecommendationInstrument.MULTI_LEG_OPTION,
        action=RecommendationAction.MULTI_LEG,
        multi_leg=spec,
    )
    assert rec.multi_leg is not None
    assert rec.action == RecommendationAction.MULTI_LEG


def test_pair_requires_exactly_two_legs() -> None:
    leg = PairLeg(symbol="AAPL", side="long", weight=Decimal("0.5"))
    with pytest.raises(ValidationError):
        _recommendation(
            instrument=RecommendationInstrument.PAIR,
            action=RecommendationAction.MULTI_LEG,
            pair_legs=(leg,),
        )


def test_pair_payload_outside_pair_instrument_rejected() -> None:
    legs = (
        PairLeg(symbol="AAPL", side="long", weight=Decimal("0.5")),
        PairLeg(symbol="MSFT", side="short", weight=Decimal("0.5")),
    )
    with pytest.raises(ValidationError):
        _recommendation(pair_legs=legs)


def test_action_item_order_type_consistency() -> None:
    with pytest.raises(ValidationError):
        ActionItem(
            action_id=f"act_{uuid.uuid4().hex[:16]}",
            track_id="balanced",
            symbol="AAPL",
            instrument=RecommendationInstrument.EQUITY,
            side=OrderSide.BUY,
            target_pct_equity=Decimal("0.1"),
            order_type=ActionOrderType.LIMIT,  # limit_price required
            rationale="Test",
        )


def test_action_item_sizing_xor() -> None:
    with pytest.raises(ValidationError):
        ActionItem(
            action_id=f"act_{uuid.uuid4().hex[:16]}",
            track_id="balanced",
            symbol="AAPL",
            instrument=RecommendationInstrument.EQUITY,
            side=OrderSide.BUY,
            target_pct_equity=Decimal("0.1"),
            target_notional_usd=Decimal("1000"),
            order_type=ActionOrderType.MARKET,
            rationale="Test",
        )


def test_recommendation_to_ledger_row_shape() -> None:
    rec = _recommendation()
    row = recommendation_to_ledger_row(rec)
    assert row["symbol"] == "AAPL"
    assert row["action"] == "buy"
    assert row["instrument_type"] == "equity"
    assert "conviction" in row
    assert "thesis" in row


def test_rule_based_fingerprint_stable_under_same_input() -> None:
    fp_a = rule_based_fingerprint(agent_id="quant", input_payload={"a": 1, "b": [1, 2]})
    fp_b = rule_based_fingerprint(agent_id="quant", input_payload={"b": [1, 2], "a": 1})
    assert fp_a.digest == fp_b.digest
