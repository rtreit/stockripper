"""Tests for :class:`MarketDataAdapter`."""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from decimal import Decimal
from typing import Any

import pytest

from stockripper.data.market_data import MarketDataAdapter


@dataclass
class _FakeBar:
    timestamp: dt.datetime
    open: float
    high: float
    low: float
    close: float
    volume: int


@dataclass
class _FakeTrade:
    price: float
    timestamp: dt.datetime


@dataclass
class _FakeDailyBar:
    volume: int


@dataclass
class _FakeSnapshot:
    latest_trade: _FakeTrade
    daily_bar: _FakeDailyBar


@dataclass
class _FakeQuote:
    bid_price: float
    ask_price: float
    timestamp: dt.datetime


class _FakeStockClient:
    def __init__(self, bars: list[_FakeBar] | None = None) -> None:
        self._bars = bars or []

    def get_stock_snapshot(self, request: Any) -> dict[str, _FakeSnapshot]:
        return {
            "AAPL": _FakeSnapshot(
                latest_trade=_FakeTrade(price=150.25, timestamp=dt.datetime.now(dt.UTC)),
                daily_bar=_FakeDailyBar(volume=1_000_000),
            )
        }

    def get_stock_bars(self, request: Any) -> dict[str, list[_FakeBar]]:
        return {"AAPL": list(self._bars)}

    def get_stock_latest_quote(self, request: Any) -> dict[str, _FakeQuote]:
        return {
            "AAPL": _FakeQuote(
                bid_price=149.99,
                ask_price=150.01,
                timestamp=dt.datetime.now(dt.UTC),
            )
        }


def _make_bars(n: int, *, close: float = 100.0, volume: int = 1_000_000) -> list[_FakeBar]:
    today = dt.datetime.now(dt.UTC)
    return [
        _FakeBar(
            timestamp=today - dt.timedelta(days=n - i),
            open=close,
            high=close,
            low=close,
            close=close,
            volume=volume,
        )
        for i in range(n)
    ]


def test_get_snapshot_normalises_alpaca_response() -> None:
    adapter = MarketDataAdapter(client=_FakeStockClient())
    snap = adapter.get_snapshot("aapl")
    assert snap.symbol == "AAPL"
    assert snap.last_price == Decimal("150.25")
    assert snap.daily_volume == 1_000_000
    assert snap.provenance.provider == "alpaca_data"
    assert len(snap.provenance.content_hash) == 64


def test_get_latest_quote_returns_provenance() -> None:
    adapter = MarketDataAdapter(client=_FakeStockClient())
    q = adapter.get_latest_quote("aapl")
    assert q.bid_price == Decimal("149.99")
    assert q.ask_price == Decimal("150.01")


def test_compute_adv_usd_averages_dollar_volume() -> None:
    bars = _make_bars(20, close=100.0, volume=1_000_000)
    adapter = MarketDataAdapter(client=_FakeStockClient(bars=bars))
    adv = adapter.compute_adv_usd("aapl", days=20)
    assert adv.adv_usd == Decimal("100000000.00")
    assert adv.bars_used == 20
    assert "insufficient_history" not in adv.provenance.data_quality_warnings


def test_compute_adv_usd_warns_on_short_history() -> None:
    bars = _make_bars(3, close=10.0, volume=10_000)
    adapter = MarketDataAdapter(client=_FakeStockClient(bars=bars))
    adv = adapter.compute_adv_usd("aapl", days=20)
    assert "insufficient_history" in adv.provenance.data_quality_warnings


def test_get_daily_bars_rejects_non_positive_days() -> None:
    adapter = MarketDataAdapter(client=_FakeStockClient())
    with pytest.raises(ValueError):
        adapter.get_daily_bars("AAPL", days=0)
