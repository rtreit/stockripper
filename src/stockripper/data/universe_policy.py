"""Per-track universe (candidate-eligibility) policy.

Deliberately kept separate from :mod:`stockripper.risk`. Risk policies
control sizing/exposure of already-chosen ideas; universe policies decide
which symbols are even *eligible* to be considered. Conflating them in
Phase 1 was tempting but would make Phase 5's risk-gate logic harder to
reason about, so they live in different modules and tables.
"""

from __future__ import annotations

from decimal import Decimal
from enum import StrEnum
from typing import Final

from pydantic import BaseModel, ConfigDict, Field


class MarketCapBand(StrEnum):
    """Coarse market-cap buckets used by per-track allow-lists."""

    MEGA = "mega"     # > $200B
    LARGE = "large"   # $10B-$200B
    MID = "mid"       # $2B-$10B
    SMALL = "small"   # $300M-$2B
    MICRO = "micro"   # $50M-$300M
    NANO = "nano"     # < $50M

    @classmethod
    def classify(cls, market_cap_usd: Decimal | float | int | None) -> MarketCapBand | None:
        """Map a market-cap value to a band, or ``None`` if unknown."""

        if market_cap_usd is None:
            return None
        cap = float(market_cap_usd)
        if cap <= 0:
            return None
        if cap > 200_000_000_000:
            return cls.MEGA
        if cap > 10_000_000_000:
            return cls.LARGE
        if cap > 2_000_000_000:
            return cls.MID
        if cap > 300_000_000:
            return cls.SMALL
        if cap > 50_000_000:
            return cls.MICRO
        return cls.NANO


class InstrumentType(StrEnum):
    """Coarse instrument allow-list per track."""

    EQUITY_LONG = "equity_long"
    EQUITY_SHORT = "equity_short"
    OPTION_SINGLE = "option_single"
    OPTION_SPREAD = "option_spread"
    ETF = "etf"
    LEVERAGED_ETF = "leveraged_etf"


_ALL_BANDS: Final[tuple[MarketCapBand, ...]] = tuple(MarketCapBand)


class UniversePolicyParams(BaseModel):
    """Per-track candidate-eligibility knobs.

    Stored in the ``risk_policies.params_json`` blob alongside risk knobs in
    Phase 2 (no schema change required), but kept as a separate Pydantic
    model so the conceptual boundary is clear.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    min_adv_usd: Decimal = Field(
        ..., description="Hard liquidity floor: 20-day average dollar volume.",
    )
    price_floor_usd: Decimal = Field(
        ..., description="Reject penny names below this last price.",
    )
    market_cap_bands_allowed: tuple[MarketCapBand, ...] = Field(
        default=_ALL_BANDS,
        description="Which market-cap bands are eligible.",
    )
    instrument_types_allowed: tuple[InstrumentType, ...] = Field(
        ..., description="Which instrument types this track may trade.",
    )
    low_visibility_enabled: bool = Field(
        default=False,
        description="Whether the low-visibility (hidden-gem) bucket is searched.",
    )
    low_visibility_max_news_30d: int = Field(
        default=2,
        ge=0,
        description="Symbols with strictly fewer than this many news items in 30d qualify as low-visibility.",
    )
    require_recent_catalyst_days: int | None = Field(
        default=None,
        description=(
            "If set, the low-visibility bucket additionally requires an 8-K within "
            "this many days. ``None`` disables that requirement."
        ),
    )


# ---------------------------------------------------------------------------
# Defaults — one per Phase-1 strategy track.
# ---------------------------------------------------------------------------
DEFAULT_UNIVERSE_POLICIES: Final[dict[str, UniversePolicyParams]] = {
    "conservative": UniversePolicyParams(
        min_adv_usd=Decimal("25000000"),
        price_floor_usd=Decimal("10"),
        market_cap_bands_allowed=(MarketCapBand.MEGA, MarketCapBand.LARGE),
        instrument_types_allowed=(InstrumentType.EQUITY_LONG, InstrumentType.ETF),
        low_visibility_enabled=False,
    ),
    "balanced": UniversePolicyParams(
        min_adv_usd=Decimal("10000000"),
        price_floor_usd=Decimal("5"),
        market_cap_bands_allowed=(MarketCapBand.MEGA, MarketCapBand.LARGE, MarketCapBand.MID),
        instrument_types_allowed=(
            InstrumentType.EQUITY_LONG,
            InstrumentType.ETF,
            InstrumentType.OPTION_SINGLE,
        ),
        low_visibility_enabled=False,
    ),
    "aggressive": UniversePolicyParams(
        min_adv_usd=Decimal("2000000"),
        price_floor_usd=Decimal("2"),
        market_cap_bands_allowed=(
            MarketCapBand.MEGA,
            MarketCapBand.LARGE,
            MarketCapBand.MID,
            MarketCapBand.SMALL,
        ),
        instrument_types_allowed=(
            InstrumentType.EQUITY_LONG,
            InstrumentType.EQUITY_SHORT,
            InstrumentType.ETF,
            InstrumentType.LEVERAGED_ETF,
            InstrumentType.OPTION_SINGLE,
            InstrumentType.OPTION_SPREAD,
        ),
        low_visibility_enabled=True,
        low_visibility_max_news_30d=3,
        require_recent_catalyst_days=14,
    ),
    "concentrated": UniversePolicyParams(
        min_adv_usd=Decimal("10000000"),
        price_floor_usd=Decimal("5"),
        market_cap_bands_allowed=(
            MarketCapBand.MEGA,
            MarketCapBand.LARGE,
            MarketCapBand.MID,
        ),
        instrument_types_allowed=(
            InstrumentType.EQUITY_LONG,
            InstrumentType.EQUITY_SHORT,
            InstrumentType.OPTION_SINGLE,
            InstrumentType.OPTION_SPREAD,
        ),
        low_visibility_enabled=False,
    ),
    "yolo": UniversePolicyParams(
        min_adv_usd=Decimal("500000"),
        price_floor_usd=Decimal("0.5"),
        market_cap_bands_allowed=_ALL_BANDS,
        instrument_types_allowed=tuple(InstrumentType),
        low_visibility_enabled=True,
        low_visibility_max_news_30d=10,
        require_recent_catalyst_days=None,
    ),
    "quant_signal": UniversePolicyParams(
        min_adv_usd=Decimal("5000000"),
        price_floor_usd=Decimal("3"),
        market_cap_bands_allowed=(
            MarketCapBand.MEGA,
            MarketCapBand.LARGE,
            MarketCapBand.MID,
            MarketCapBand.SMALL,
        ),
        instrument_types_allowed=(
            InstrumentType.EQUITY_LONG,
            InstrumentType.EQUITY_SHORT,
            InstrumentType.ETF,
        ),
        low_visibility_enabled=False,
    ),
    "random_baseline": UniversePolicyParams(
        min_adv_usd=Decimal("10000000"),
        price_floor_usd=Decimal("5"),
        market_cap_bands_allowed=(
            MarketCapBand.MEGA,
            MarketCapBand.LARGE,
            MarketCapBand.MID,
        ),
        instrument_types_allowed=(InstrumentType.EQUITY_LONG, InstrumentType.ETF),
        low_visibility_enabled=False,
    ),
    "benchmark": UniversePolicyParams(
        min_adv_usd=Decimal("50000000"),
        price_floor_usd=Decimal("20"),
        market_cap_bands_allowed=(MarketCapBand.MEGA, MarketCapBand.LARGE),
        instrument_types_allowed=(InstrumentType.ETF,),
        low_visibility_enabled=False,
    ),
}


__all__ = (
    "DEFAULT_UNIVERSE_POLICIES",
    "InstrumentType",
    "MarketCapBand",
    "UniversePolicyParams",
)
