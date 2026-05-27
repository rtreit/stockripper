"""Derive a small, honest set of fundamentals from EDGAR company-facts.

Phase 2 contract: each derived datum is a :class:`FundamentalValue`
carrying its as-of date, the source XBRL tag, a confidence label, and any
data-quality warnings the deriver wants to flag. When the underlying facts
are missing or ambiguous we return ``None`` rather than fabricate.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from decimal import Decimal
from typing import Any, Literal

from stockripper.data.provenance import Provenance
from stockripper.data.sec_edgar import CompanyFacts

Confidence = Literal["high", "medium", "low"]


@dataclass(frozen=True)
class FundamentalValue:
    """A single derived fundamental with measurement metadata."""

    value: Decimal
    as_of: dt.date
    source_fact: str
    unit: str
    confidence: Confidence
    data_quality_warnings: tuple[str, ...] = ()


@dataclass(frozen=True)
class FundamentalsSummary:
    """Small set of derived ratios + provenance reference back to EDGAR."""

    cik: str
    entity_name: str | None
    shares_outstanding: FundamentalValue | None
    revenue_ttm: FundamentalValue | None
    net_income_ttm: FundamentalValue | None
    total_debt: FundamentalValue | None
    total_equity: FundamentalValue | None
    debt_to_equity: FundamentalValue | None
    market_cap: FundamentalValue | None
    provenance: Provenance


_TAG_SHARES_OUT: tuple[str, ...] = (
    "CommonStockSharesOutstanding",
    "EntityCommonStockSharesOutstanding",
)
_TAG_REVENUE: tuple[str, ...] = (
    "Revenues",
    "RevenueFromContractWithCustomerExcludingAssessedTax",
    "SalesRevenueNet",
)
_TAG_NET_INCOME: tuple[str, ...] = (
    "NetIncomeLoss",
)
_TAG_DEBT_LONG: tuple[str, ...] = ("LongTermDebt", "LongTermDebtNoncurrent")
_TAG_DEBT_SHORT: tuple[str, ...] = ("ShortTermBorrowings", "LongTermDebtCurrent")
_TAG_EQUITY: tuple[str, ...] = (
    "StockholdersEquity",
    "StockholdersEquityIncludingPortionAttributableToNoncontrollingInterest",
)


def derive_fundamentals(
    facts: CompanyFacts,
    *,
    latest_price: Decimal | None = None,
) -> FundamentalsSummary:
    """Compute the Phase-2 fundamentals summary from EDGAR ``CompanyFacts``."""

    us_gaap = facts.facts.get("us-gaap", {}) or {}
    dei = facts.facts.get("dei", {}) or {}

    shares = _pick_instantaneous(dei, _TAG_SHARES_OUT, units=("shares",))
    if shares is None:
        shares = _pick_instantaneous(us_gaap, _TAG_SHARES_OUT, units=("shares",))
    revenue = _pick_trailing_ttm(us_gaap, _TAG_REVENUE, units=("USD",))
    net_income = _pick_trailing_ttm(us_gaap, _TAG_NET_INCOME, units=("USD",))
    long_debt = _pick_instantaneous(us_gaap, _TAG_DEBT_LONG, units=("USD",))
    short_debt = _pick_instantaneous(us_gaap, _TAG_DEBT_SHORT, units=("USD",))
    equity = _pick_instantaneous(us_gaap, _TAG_EQUITY, units=("USD",))

    total_debt = _combine_sum(long_debt, short_debt, fact_label="LongTermDebt+ShortTermBorrowings")

    debt_to_equity: FundamentalValue | None = None
    if total_debt is not None and equity is not None and equity.value != 0:
        ratio = (total_debt.value / equity.value).quantize(Decimal("0.0001"))
        warnings: tuple[str, ...] = ()
        if total_debt.as_of != equity.as_of:
            warnings = ("debt_equity_periods_differ",)
        debt_to_equity = FundamentalValue(
            value=ratio,
            as_of=min(total_debt.as_of, equity.as_of),
            source_fact="debt/equity",
            unit="ratio",
            confidence="medium" if not warnings else "low",
            data_quality_warnings=warnings,
        )

    market_cap: FundamentalValue | None = None
    if shares is not None and latest_price is not None and latest_price > 0:
        warnings = ()
        stale_days = (dt.date.today() - shares.as_of).days
        if stale_days > 120:
            warnings = ("shares_out_stale",)
        market_cap = FundamentalValue(
            value=(shares.value * latest_price).quantize(Decimal("0.01")),
            as_of=shares.as_of,
            source_fact=f"price*{shares.source_fact}",
            unit="USD",
            confidence="medium" if not warnings else "low",
            data_quality_warnings=warnings,
        )

    return FundamentalsSummary(
        cik=facts.cik,
        entity_name=facts.entity_name,
        shares_outstanding=shares,
        revenue_ttm=revenue,
        net_income_ttm=net_income,
        total_debt=total_debt,
        total_equity=equity,
        debt_to_equity=debt_to_equity,
        market_cap=market_cap,
        provenance=facts.provenance,
    )


# ---------------------------------------------------------------------------
# Tag pickers
# ---------------------------------------------------------------------------
def _pick_instantaneous(
    namespace: dict[str, Any],
    tags: tuple[str, ...],
    *,
    units: tuple[str, ...],
) -> FundamentalValue | None:
    """Return the most recent instantaneous-period fact from any of ``tags``."""

    for tag in tags:
        fact = namespace.get(tag)
        if not fact:
            continue
        for unit in units:
            entries = (fact.get("units") or {}).get(unit) or []
            inst = [e for e in entries if e.get("fp") in {None, "FY"} or ("end" in e and "start" not in e)]
            if not inst:
                inst = [e for e in entries if "end" in e]
            if not inst:
                continue
            entry = max(inst, key=lambda e: e.get("end", ""))
            try:
                value = Decimal(str(entry["val"]))
                as_of = dt.date.fromisoformat(entry["end"])
            except (KeyError, ValueError, ArithmeticError):
                continue
            return FundamentalValue(
                value=value,
                as_of=as_of,
                source_fact=tag,
                unit=unit,
                confidence="high",
            )
    return None


def _pick_trailing_ttm(
    namespace: dict[str, Any],
    tags: tuple[str, ...],
    *,
    units: tuple[str, ...],
) -> FundamentalValue | None:
    """Approximate TTM by taking the most recent annual ``FY`` value.

    Computing a true trailing-twelve-month sum from quarterly facts is
    Phase-3 work — for Phase 2 we surface the most recent FY value and tag
    it with ``proxy_fy`` so the deriver is honest about what it returned.
    """

    for tag in tags:
        fact = namespace.get(tag)
        if not fact:
            continue
        for unit in units:
            entries = (fact.get("units") or {}).get(unit) or []
            annual = [e for e in entries if e.get("fp") == "FY"]
            if not annual:
                continue
            entry = max(annual, key=lambda e: e.get("end", ""))
            try:
                value = Decimal(str(entry["val"]))
                as_of = dt.date.fromisoformat(entry["end"])
            except (KeyError, ValueError, ArithmeticError):
                continue
            return FundamentalValue(
                value=value,
                as_of=as_of,
                source_fact=f"{tag}@FY",
                unit=unit,
                confidence="medium",
                data_quality_warnings=("proxy_fy",),
            )
    return None


def _combine_sum(
    a: FundamentalValue | None,
    b: FundamentalValue | None,
    *,
    fact_label: str,
) -> FundamentalValue | None:
    if a is None and b is None:
        return None
    if a is None:
        return b
    if b is None:
        return a
    warnings: tuple[str, ...] = ()
    if a.as_of != b.as_of:
        warnings = ("component_periods_differ",)
    return FundamentalValue(
        value=a.value + b.value,
        as_of=min(a.as_of, b.as_of),
        source_fact=fact_label,
        unit=a.unit,
        confidence="medium" if not warnings else "low",
        data_quality_warnings=warnings,
    )


__all__ = (
    "Confidence",
    "FundamentalValue",
    "FundamentalsSummary",
    "derive_fundamentals",
)
