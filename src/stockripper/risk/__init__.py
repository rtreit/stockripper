"""Per-track risk-policy data model and default policies for the 8 MVP tracks.

Risk policies are Pydantic models stored as JSON in ``risk_policies.params_json``.
Per-track parameters live here as defaults so the spec's §5.3 table is the
authority and the registry can re-seed an empty database deterministically.
"""

from __future__ import annotations

from decimal import Decimal
from typing import Final

from pydantic import BaseModel, ConfigDict, Field


class RiskPolicyParams(BaseModel):
    """Per-track risk-policy knobs.

    These are configuration values consumed by the per-track risk gate in
    Phase 5; Phase 1 only persists them so future phases can reach for them
    without a schema change.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    max_position_pct_equity: Decimal = Field(
        ..., description="Hard cap on a single position as a fraction of track equity.",
    )
    min_holdings: int = Field(
        ..., ge=1, description="Minimum number of open positions (diversification floor).",
    )
    max_holdings: int = Field(
        ..., ge=1, description="Maximum number of open positions.",
    )
    max_gross_exposure_pct_equity: Decimal = Field(
        ..., description="Cap on |long| + |short| as a fraction of equity.",
    )
    max_short_exposure_pct_equity: Decimal = Field(
        ..., description="Cap on short notional as a fraction of equity (0 disables shorts).",
    )
    max_options_notional_pct_equity: Decimal = Field(
        ..., description="Cap on options notional as a fraction of equity (0 disables options).",
    )
    leveraged_etf_allowed: bool = Field(
        ..., description="Whether leveraged or inverse ETFs may be held.",
    )
    max_daily_turnover_pct_equity: Decimal | None = Field(
        default=None,
        description=(
            "Soft cap on per-day notional turnover relative to equity; "
            "``None`` means unbounded (e.g., YOLO track)."
        ),
    )
    judge_objective: str = Field(
        ..., description="Optimization target the per-track judge maximises.",
    )


# ----------------------------------------------------------------------
# Default policies — one per MVP track from spec §5.3.
# ----------------------------------------------------------------------
DEFAULT_RISK_POLICIES: Final[dict[str, RiskPolicyParams]] = {
    "rp_conservative": RiskPolicyParams(
        max_position_pct_equity=Decimal("0.03"),
        min_holdings=30,
        max_holdings=80,
        max_gross_exposure_pct_equity=Decimal("1.00"),
        max_short_exposure_pct_equity=Decimal("0.00"),
        max_options_notional_pct_equity=Decimal("0.00"),
        leveraged_etf_allowed=False,
        max_daily_turnover_pct_equity=Decimal("0.05"),
        judge_objective="sharpe",
    ),
    "rp_balanced": RiskPolicyParams(
        max_position_pct_equity=Decimal("0.07"),
        min_holdings=15,
        max_holdings=30,
        max_gross_exposure_pct_equity=Decimal("1.20"),
        max_short_exposure_pct_equity=Decimal("0.00"),
        max_options_notional_pct_equity=Decimal("0.10"),
        leveraged_etf_allowed=False,
        max_daily_turnover_pct_equity=Decimal("0.15"),
        judge_objective="sortino",
    ),
    "rp_aggressive": RiskPolicyParams(
        max_position_pct_equity=Decimal("0.15"),
        min_holdings=8,
        max_holdings=20,
        max_gross_exposure_pct_equity=Decimal("2.00"),
        max_short_exposure_pct_equity=Decimal("0.50"),
        max_options_notional_pct_equity=Decimal("0.40"),
        leveraged_etf_allowed=True,
        max_daily_turnover_pct_equity=Decimal("0.60"),
        judge_objective="return",
    ),
    "rp_concentrated": RiskPolicyParams(
        max_position_pct_equity=Decimal("0.35"),
        min_holdings=3,
        max_holdings=10,
        max_gross_exposure_pct_equity=Decimal("1.50"),
        max_short_exposure_pct_equity=Decimal("0.30"),
        max_options_notional_pct_equity=Decimal("0.30"),
        leveraged_etf_allowed=True,
        max_daily_turnover_pct_equity=Decimal("0.30"),
        judge_objective="calmar",
    ),
    "rp_yolo": RiskPolicyParams(
        max_position_pct_equity=Decimal("1.00"),
        min_holdings=1,
        max_holdings=10,
        max_gross_exposure_pct_equity=Decimal("4.00"),
        max_short_exposure_pct_equity=Decimal("2.00"),
        max_options_notional_pct_equity=Decimal("3.00"),
        leveraged_etf_allowed=True,
        max_daily_turnover_pct_equity=None,
        judge_objective="return",
    ),
    "rp_quant_signal": RiskPolicyParams(
        max_position_pct_equity=Decimal("0.05"),
        min_holdings=20,
        max_holdings=60,
        max_gross_exposure_pct_equity=Decimal("2.00"),
        max_short_exposure_pct_equity=Decimal("1.00"),
        max_options_notional_pct_equity=Decimal("0.00"),
        leveraged_etf_allowed=False,
        max_daily_turnover_pct_equity=Decimal("1.00"),
        judge_objective="sharpe",
    ),
    "rp_random_baseline": RiskPolicyParams(
        max_position_pct_equity=Decimal("0.05"),
        min_holdings=20,
        max_holdings=20,
        max_gross_exposure_pct_equity=Decimal("1.00"),
        max_short_exposure_pct_equity=Decimal("0.00"),
        max_options_notional_pct_equity=Decimal("0.00"),
        leveraged_etf_allowed=False,
        max_daily_turnover_pct_equity=Decimal("0.10"),
        judge_objective="return",
    ),
    "rp_benchmark": RiskPolicyParams(
        max_position_pct_equity=Decimal("0.40"),
        min_holdings=3,
        max_holdings=4,
        max_gross_exposure_pct_equity=Decimal("1.00"),
        max_short_exposure_pct_equity=Decimal("0.00"),
        max_options_notional_pct_equity=Decimal("0.00"),
        leveraged_etf_allowed=False,
        max_daily_turnover_pct_equity=Decimal("0.02"),
        judge_objective="return",
    ),
}


__all__ = ("DEFAULT_RISK_POLICIES", "RiskPolicyParams")
