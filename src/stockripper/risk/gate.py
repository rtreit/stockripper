"""Per-track risk gate (spec §16.2).

Evaluates a single :class:`ActionItem` against the per-track
:class:`RiskPolicyParams` and current :class:`PortfolioState`. The output
is a structured :class:`RiskDecision` (approved or rejected with reason
codes) that the execution adapter persists onto
``decision_actions.risk_status`` before submitting any order.

Hard rule: the risk gate **never** mutates inputs and **never** decides
on its own to submit anything. It only answers "is this allowed under
this track's policy right now?".
"""

from __future__ import annotations

from dataclasses import dataclass, field
from decimal import Decimal
from enum import StrEnum
from typing import Final

from stockripper.agents.schemas import (
    ActionItem,
    OrderSide,
    RecommendationInstrument,
)
from stockripper.risk import RiskPolicyParams
from stockripper.risk.portfolio import PortfolioState

_SHORT_SIDES: Final[frozenset[OrderSide]] = frozenset(
    {OrderSide.SELL_SHORT, OrderSide.BUY_TO_COVER}
)
_OPTION_INSTRUMENTS: Final[frozenset[RecommendationInstrument]] = frozenset(
    {RecommendationInstrument.OPTION_SINGLE, RecommendationInstrument.MULTI_LEG_OPTION}
)


class RiskDecisionStatus(StrEnum):
    APPROVED = "approved"
    REJECTED = "rejected"


class RiskRejectionCode(StrEnum):
    """Structured per-track rejection codes.

    Distinct from :class:`stockripper.risk.floors.FloorCode`: floors are
    universal invariants; these are per-track policy violations and can
    be loosened by configuring the policy.
    """

    POSITION_PCT_EQUITY = "rt_position_pct_equity"
    SHORT_DISALLOWED = "rt_short_disallowed"
    SHORT_EXPOSURE_PCT_EQUITY = "rt_short_exposure_pct_equity"
    OPTIONS_DISALLOWED = "rt_options_disallowed"
    OPTIONS_NOTIONAL_PCT_EQUITY = "rt_options_notional_pct_equity"
    LEVERAGED_ETF_DISALLOWED = "rt_leveraged_etf_disallowed"
    GROSS_EXPOSURE_PCT_EQUITY = "rt_gross_exposure_pct_equity"
    MAX_HOLDINGS = "rt_max_holdings"
    SIZING_MISSING_PRICE = "rt_sizing_missing_price"


@dataclass(frozen=True)
class RiskRejection:
    code: RiskRejectionCode
    message: str
    cap: Decimal | None = None
    observed: Decimal | None = None


@dataclass(frozen=True)
class RiskDecision:
    status: RiskDecisionStatus
    rejections: tuple[RiskRejection, ...] = field(default_factory=tuple)
    approved_notional_usd: Decimal | None = None
    """The notional we would submit if approved (resolved from pct_equity if needed)."""

    @property
    def is_approved(self) -> bool:
        return self.status is RiskDecisionStatus.APPROVED

    @property
    def is_rejected(self) -> bool:
        return self.status is RiskDecisionStatus.REJECTED

    def summary(self) -> str:
        if self.is_approved:
            return "approved"
        codes = ",".join(r.code.value for r in self.rejections)
        return f"rejected:{codes}"


@dataclass(frozen=True)
class RiskGate:
    """Pure-evaluation gate. Construct once per policy; reuse per action."""

    policy: RiskPolicyParams

    def evaluate(
        self,
        *,
        action: ActionItem,
        portfolio: PortfolioState,
    ) -> RiskDecision:
        rejections: list[RiskRejection] = []
        target_notional = _resolve_target_notional(action, portfolio)
        if target_notional is None:
            # Either both fields set (caught by schema) or pct given with
            # equity == 0 — refuse on sizing.
            return RiskDecision(
                status=RiskDecisionStatus.REJECTED,
                rejections=(
                    RiskRejection(
                        code=RiskRejectionCode.SIZING_MISSING_PRICE,
                        message="cannot resolve target notional from action+portfolio",
                    ),
                ),
            )

        # --- per-position cap ---
        position_cap = self.policy.max_position_pct_equity * portfolio.equity
        if target_notional > position_cap:
            rejections.append(
                RiskRejection(
                    code=RiskRejectionCode.POSITION_PCT_EQUITY,
                    message=(
                        f"target ${target_notional} exceeds "
                        f"max_position_pct_equity={self.policy.max_position_pct_equity} "
                        f"(cap ${position_cap})"
                    ),
                    cap=position_cap,
                    observed=target_notional,
                )
            )

        # --- short permissions ---
        if action.side in _SHORT_SIDES:
            short_cap = self.policy.max_short_exposure_pct_equity * portfolio.equity
            if self.policy.max_short_exposure_pct_equity <= Decimal("0"):
                rejections.append(
                    RiskRejection(
                        code=RiskRejectionCode.SHORT_DISALLOWED,
                        message="track policy disallows short selling",
                        cap=Decimal("0"),
                        observed=target_notional,
                    )
                )
            else:
                projected_short = portfolio.short_exposure + target_notional
                if projected_short > short_cap:
                    rejections.append(
                        RiskRejection(
                            code=RiskRejectionCode.SHORT_EXPOSURE_PCT_EQUITY,
                            message=(
                                f"projected short exposure ${projected_short} exceeds "
                                f"max_short_exposure_pct_equity="
                                f"{self.policy.max_short_exposure_pct_equity} "
                                f"(cap ${short_cap})"
                            ),
                            cap=short_cap,
                            observed=projected_short,
                        )
                    )

        # --- options permissions ---
        if action.instrument in _OPTION_INSTRUMENTS:
            options_cap = self.policy.max_options_notional_pct_equity * portfolio.equity
            if self.policy.max_options_notional_pct_equity <= Decimal("0"):
                rejections.append(
                    RiskRejection(
                        code=RiskRejectionCode.OPTIONS_DISALLOWED,
                        message="track policy disallows options",
                        cap=Decimal("0"),
                        observed=target_notional,
                    )
                )
            else:
                projected_opt = portfolio.options_notional + target_notional
                if projected_opt > options_cap:
                    rejections.append(
                        RiskRejection(
                            code=RiskRejectionCode.OPTIONS_NOTIONAL_PCT_EQUITY,
                            message=(
                                f"projected options notional ${projected_opt} exceeds "
                                f"max_options_notional_pct_equity="
                                f"{self.policy.max_options_notional_pct_equity} "
                                f"(cap ${options_cap})"
                            ),
                            cap=options_cap,
                            observed=projected_opt,
                        )
                    )

        # --- leveraged ETF permission ---
        if action.instrument == RecommendationInstrument.LEVERAGED_ETF and not self.policy.leveraged_etf_allowed:
            rejections.append(
                RiskRejection(
                    code=RiskRejectionCode.LEVERAGED_ETF_DISALLOWED,
                    message="track policy disallows leveraged ETFs",
                )
            )

        # --- gross exposure cap (after this action) ---
        gross_cap = self.policy.max_gross_exposure_pct_equity * portfolio.equity
        projected_gross = portfolio.gross_exposure + target_notional
        if projected_gross > gross_cap:
            rejections.append(
                RiskRejection(
                    code=RiskRejectionCode.GROSS_EXPOSURE_PCT_EQUITY,
                    message=(
                        f"projected gross exposure ${projected_gross} exceeds "
                        f"max_gross_exposure_pct_equity="
                        f"{self.policy.max_gross_exposure_pct_equity} "
                        f"(cap ${gross_cap})"
                    ),
                    cap=gross_cap,
                    observed=projected_gross,
                )
            )

        # --- max holdings (after this action, only for opening a new position) ---
        if portfolio.position(action.symbol) is None and action.side in {
            OrderSide.BUY,
            OrderSide.SELL_SHORT,
        }:
            projected_holdings = len(portfolio.positions) + 1
            if projected_holdings > self.policy.max_holdings:
                rejections.append(
                    RiskRejection(
                        code=RiskRejectionCode.MAX_HOLDINGS,
                        message=(
                            f"projected holdings count {projected_holdings} exceeds "
                            f"max_holdings={self.policy.max_holdings}"
                        ),
                        cap=Decimal(self.policy.max_holdings),
                        observed=Decimal(projected_holdings),
                    )
                )

        if rejections:
            return RiskDecision(
                status=RiskDecisionStatus.REJECTED,
                rejections=tuple(rejections),
            )
        return RiskDecision(
            status=RiskDecisionStatus.APPROVED,
            approved_notional_usd=target_notional,
        )


def _resolve_target_notional(
    action: ActionItem,
    portfolio: PortfolioState,
) -> Decimal | None:
    if action.target_notional_usd is not None:
        return action.target_notional_usd
    if action.target_pct_equity is not None and portfolio.equity > 0:
        return (action.target_pct_equity * portfolio.equity).quantize(Decimal("0.01"))
    return None


__all__ = (
    "RiskDecision",
    "RiskDecisionStatus",
    "RiskGate",
    "RiskRejection",
    "RiskRejectionCode",
)
