"""Tests for the per-track risk gate (spec §16.2)."""

from __future__ import annotations

from decimal import Decimal

from stockripper.agents.schemas import (
    ActionItem,
    ActionOrderType,
    OrderSide,
    RecommendationInstrument,
)
from stockripper.risk import DEFAULT_RISK_POLICIES, RiskPolicyParams
from stockripper.risk.gate import RiskGate, RiskRejectionCode
from stockripper.risk.portfolio import PortfolioState, Position


def _flat_portfolio(equity: Decimal = Decimal("100000")) -> PortfolioState:
    return PortfolioState(track_id="balanced", equity=equity, cash=equity)


def _action(
    *,
    instrument: RecommendationInstrument = RecommendationInstrument.EQUITY,
    side: OrderSide = OrderSide.BUY,
    target_notional_usd: Decimal | None = Decimal("1000"),
    target_pct_equity: Decimal | None = None,
    symbol: str = "AAPL",
) -> ActionItem:
    kwargs: dict[str, object] = {
        "action_id": "act_x",
        "track_id": "balanced",
        "symbol": symbol,
        "instrument": instrument,
        "side": side,
        "order_type": ActionOrderType.MARKET,
        "rationale": "test",
    }
    if target_notional_usd is not None:
        kwargs["target_notional_usd"] = target_notional_usd
    if target_pct_equity is not None:
        kwargs["target_pct_equity"] = target_pct_equity
    return ActionItem(**kwargs)  # type: ignore[arg-type]


def test_gate_approves_within_caps() -> None:
    gate = RiskGate(policy=DEFAULT_RISK_POLICIES["rp_balanced"])
    decision = gate.evaluate(
        action=_action(target_notional_usd=Decimal("5000")),
        portfolio=_flat_portfolio(),
    )
    assert decision.is_approved
    assert decision.approved_notional_usd == Decimal("5000")
    assert decision.summary() == "approved"


def test_gate_resolves_pct_equity_to_notional() -> None:
    gate = RiskGate(policy=DEFAULT_RISK_POLICIES["rp_balanced"])
    decision = gate.evaluate(
        action=_action(
            target_notional_usd=None,
            target_pct_equity=Decimal("0.05"),
        ),
        portfolio=_flat_portfolio(Decimal("100000")),
    )
    assert decision.is_approved
    assert decision.approved_notional_usd == Decimal("5000.00")


def test_gate_rejects_oversized_position() -> None:
    gate = RiskGate(policy=DEFAULT_RISK_POLICIES["rp_balanced"])
    # Balanced cap is 7% of equity. $10k on $100k portfolio = 10%.
    decision = gate.evaluate(
        action=_action(target_notional_usd=Decimal("10000")),
        portfolio=_flat_portfolio(),
    )
    assert decision.is_rejected
    codes = {r.code for r in decision.rejections}
    assert RiskRejectionCode.POSITION_PCT_EQUITY in codes


def test_gate_rejects_short_when_disallowed() -> None:
    gate = RiskGate(policy=DEFAULT_RISK_POLICIES["rp_balanced"])
    decision = gate.evaluate(
        action=_action(side=OrderSide.SELL_SHORT),
        portfolio=_flat_portfolio(),
    )
    assert decision.is_rejected
    codes = {r.code for r in decision.rejections}
    assert RiskRejectionCode.SHORT_DISALLOWED in codes


def test_gate_rejects_options_when_disallowed() -> None:
    gate = RiskGate(policy=DEFAULT_RISK_POLICIES["rp_conservative"])
    decision = gate.evaluate(
        action=_action(instrument=RecommendationInstrument.OPTION_SINGLE),
        portfolio=_flat_portfolio(),
    )
    assert decision.is_rejected
    codes = {r.code for r in decision.rejections}
    assert RiskRejectionCode.OPTIONS_DISALLOWED in codes


def test_gate_rejects_leveraged_etf_when_disallowed() -> None:
    gate = RiskGate(policy=DEFAULT_RISK_POLICIES["rp_balanced"])
    decision = gate.evaluate(
        action=_action(instrument=RecommendationInstrument.LEVERAGED_ETF),
        portfolio=_flat_portfolio(),
    )
    assert decision.is_rejected
    codes = {r.code for r in decision.rejections}
    assert RiskRejectionCode.LEVERAGED_ETF_DISALLOWED in codes


def test_gate_approves_leveraged_etf_when_allowed() -> None:
    gate = RiskGate(policy=DEFAULT_RISK_POLICIES["rp_aggressive"])
    decision = gate.evaluate(
        action=_action(instrument=RecommendationInstrument.LEVERAGED_ETF),
        portfolio=_flat_portfolio(),
    )
    assert decision.is_approved


def test_gate_rejects_when_short_cap_breached() -> None:
    # Aggressive policy allows shorts up to 50% of equity.
    gate = RiskGate(policy=DEFAULT_RISK_POLICIES["rp_aggressive"])
    # Existing short positions already at 49% of $100k.
    portfolio = PortfolioState(
        track_id="aggressive",
        equity=Decimal("100000"),
        cash=Decimal("100000"),
        positions=(
            Position(
                symbol="SPY",
                qty=Decimal("-100"),
                market_value=Decimal("-49000"),
            ),
        ),
    )
    decision = gate.evaluate(
        action=_action(
            side=OrderSide.SELL_SHORT,
            symbol="QQQ",
            target_notional_usd=Decimal("5000"),
        ),
        portfolio=portfolio,
    )
    assert decision.is_rejected
    codes = {r.code for r in decision.rejections}
    assert RiskRejectionCode.SHORT_EXPOSURE_PCT_EQUITY in codes


def test_gate_rejects_when_max_holdings_exceeded() -> None:
    """Opening a new position when already at the per-track holdings cap."""

    policy = RiskPolicyParams(
        max_position_pct_equity=Decimal("0.50"),
        min_holdings=1,
        max_holdings=2,
        max_gross_exposure_pct_equity=Decimal("4.00"),
        max_short_exposure_pct_equity=Decimal("0.00"),
        max_options_notional_pct_equity=Decimal("0.00"),
        leveraged_etf_allowed=False,
        judge_objective="return",
    )
    gate = RiskGate(policy=policy)
    portfolio = PortfolioState(
        track_id="t",
        equity=Decimal("100000"),
        cash=Decimal("50000"),
        positions=(
            Position(symbol="AAPL", qty=Decimal("10"), market_value=Decimal("2000")),
            Position(symbol="MSFT", qty=Decimal("5"), market_value=Decimal("1500")),
        ),
    )
    decision = gate.evaluate(
        action=_action(symbol="GOOG", target_notional_usd=Decimal("1000")),
        portfolio=portfolio,
    )
    assert decision.is_rejected
    codes = {r.code for r in decision.rejections}
    assert RiskRejectionCode.MAX_HOLDINGS in codes


def test_gate_allows_adding_to_existing_position_at_max_holdings() -> None:
    """Buying more of an already-held name should not trip max_holdings."""

    policy = RiskPolicyParams(
        max_position_pct_equity=Decimal("0.50"),
        min_holdings=1,
        max_holdings=2,
        max_gross_exposure_pct_equity=Decimal("4.00"),
        max_short_exposure_pct_equity=Decimal("0.00"),
        max_options_notional_pct_equity=Decimal("0.00"),
        leveraged_etf_allowed=False,
        judge_objective="return",
    )
    gate = RiskGate(policy=policy)
    portfolio = PortfolioState(
        track_id="t",
        equity=Decimal("100000"),
        cash=Decimal("50000"),
        positions=(
            Position(symbol="AAPL", qty=Decimal("10"), market_value=Decimal("2000")),
            Position(symbol="MSFT", qty=Decimal("5"), market_value=Decimal("1500")),
        ),
    )
    # Adding to AAPL (already held), not opening a new name.
    decision = gate.evaluate(
        action=_action(symbol="AAPL", target_notional_usd=Decimal("1000")),
        portfolio=portfolio,
    )
    assert decision.is_approved


def test_gate_rejects_when_gross_exposure_cap_breached() -> None:
    """Gross-exposure cap protects against fully-margined chaining."""

    policy = RiskPolicyParams(
        max_position_pct_equity=Decimal("1.00"),
        min_holdings=1,
        max_holdings=20,
        max_gross_exposure_pct_equity=Decimal("1.00"),
        max_short_exposure_pct_equity=Decimal("0.00"),
        max_options_notional_pct_equity=Decimal("0.00"),
        leveraged_etf_allowed=False,
        judge_objective="return",
    )
    gate = RiskGate(policy=policy)
    portfolio = PortfolioState(
        track_id="t",
        equity=Decimal("100000"),
        cash=Decimal("5000"),
        positions=(
            Position(symbol="SPY", qty=Decimal("200"), market_value=Decimal("95000")),
        ),
    )
    decision = gate.evaluate(
        action=_action(symbol="QQQ", target_notional_usd=Decimal("10000")),
        portfolio=portfolio,
    )
    assert decision.is_rejected
    codes = {r.code for r in decision.rejections}
    assert RiskRejectionCode.GROSS_EXPOSURE_PCT_EQUITY in codes
