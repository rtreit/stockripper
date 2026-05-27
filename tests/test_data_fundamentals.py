"""Tests for :func:`derive_fundamentals`."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal
from typing import Any

from stockripper.data.fundamentals import derive_fundamentals
from stockripper.data.provenance import Provenance
from stockripper.data.sec_edgar import CompanyFacts


def _facts(payload: dict[str, Any]) -> CompanyFacts:
    return CompanyFacts(
        cik="0000123456",
        entity_name="Test Co",
        facts=payload,
        provenance=Provenance.for_payload(
            provider="sec_edgar",
            source_url="https://data.sec.gov/api/xbrl/companyfacts/CIK0000123456.json",
            payload={},
        ),
    )


def test_returns_nones_when_namespaces_empty() -> None:
    summary = derive_fundamentals(_facts({}))
    assert summary.shares_outstanding is None
    assert summary.revenue_ttm is None
    assert summary.net_income_ttm is None
    assert summary.total_debt is None
    assert summary.total_equity is None
    assert summary.debt_to_equity is None
    assert summary.market_cap is None


def test_derives_shares_revenue_and_market_cap() -> None:
    today = dt.date.today()
    recent = today - dt.timedelta(days=30)
    payload = {
        "dei": {
            "EntityCommonStockSharesOutstanding": {
                "units": {
                    "shares": [
                        {"val": 1_000_000_000, "end": recent.isoformat(), "fp": "FY"},
                    ]
                }
            }
        },
        "us-gaap": {
            "Revenues": {
                "units": {
                    "USD": [
                        {"val": 50_000_000_000, "end": recent.isoformat(), "fp": "FY"},
                    ]
                }
            },
            "NetIncomeLoss": {
                "units": {
                    "USD": [
                        {"val": 5_000_000_000, "end": recent.isoformat(), "fp": "FY"},
                    ]
                }
            },
            "LongTermDebt": {
                "units": {
                    "USD": [
                        {"val": 10_000_000_000, "end": recent.isoformat()},
                    ]
                }
            },
            "StockholdersEquity": {
                "units": {
                    "USD": [
                        {"val": 20_000_000_000, "end": recent.isoformat()},
                    ]
                }
            },
        },
    }
    summary = derive_fundamentals(_facts(payload), latest_price=Decimal("150"))
    assert summary.shares_outstanding is not None
    assert summary.shares_outstanding.value == Decimal("1000000000")
    assert summary.revenue_ttm is not None
    assert summary.revenue_ttm.value == Decimal("50000000000")
    assert "proxy_fy" in summary.revenue_ttm.data_quality_warnings
    assert summary.market_cap is not None
    assert summary.market_cap.value == Decimal("150000000000.00")
    assert summary.debt_to_equity is not None
    assert summary.debt_to_equity.value == Decimal("0.5000")


def test_market_cap_warns_when_shares_stale() -> None:
    long_ago = dt.date.today() - dt.timedelta(days=200)
    payload = {
        "dei": {
            "EntityCommonStockSharesOutstanding": {
                "units": {
                    "shares": [
                        {"val": 100_000_000, "end": long_ago.isoformat(), "fp": "FY"},
                    ]
                }
            }
        }
    }
    summary = derive_fundamentals(_facts(payload), latest_price=Decimal("10"))
    assert summary.market_cap is not None
    assert "shares_out_stale" in summary.market_cap.data_quality_warnings
    assert summary.market_cap.confidence == "low"
