"""Market-data adapter built on alpaca-py.

Returns provenance-tagged dataclasses. **Non-order-capable** — there is no
path through this module that can submit, cancel, or replace an Alpaca
order.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from decimal import Decimal
from typing import Any

from stockripper.data.provenance import Provenance
from stockripper.integrations.alpaca import (
    StockDataLike,
    build_stock_data_client,
)

_ALPACA_DATA_BASE: str = "alpaca-data://stocks"


def _utcnow() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


@dataclass(frozen=True)
class Bar:
    timestamp: dt.datetime
    open: Decimal
    high: Decimal
    low: Decimal
    close: Decimal
    volume: int

    @property
    def dollar_volume(self) -> Decimal:
        return self.close * Decimal(self.volume)


@dataclass(frozen=True)
class Quote:
    symbol: str
    bid_price: Decimal
    ask_price: Decimal
    timestamp: dt.datetime
    provenance: Provenance


@dataclass(frozen=True)
class Snapshot:
    symbol: str
    last_price: Decimal | None
    last_trade_at: dt.datetime | None
    daily_volume: int | None
    provenance: Provenance


@dataclass(frozen=True)
class AdvResult:
    """20- or 30-day average dollar volume result with provenance."""

    symbol: str
    window_days: int
    adv_usd: Decimal
    bars_used: int
    provenance: Provenance


class MarketDataAdapter:
    """Thin, typed facade over the alpaca-py historical stock-data client."""

    def __init__(self, client: StockDataLike | None = None) -> None:
        self._client = client if client is not None else build_stock_data_client()

    # ------------------------------------------------------------------
    # Snapshot / quote
    # ------------------------------------------------------------------
    def get_snapshot(self, symbol: str) -> Snapshot:
        from alpaca.data.requests import StockSnapshotRequest

        symbol = symbol.upper()
        req = StockSnapshotRequest(symbol_or_symbols=symbol)
        result = self._client.get_stock_snapshot(req)
        raw = _select(result, symbol)
        latest_trade = getattr(raw, "latest_trade", None)
        daily_bar = getattr(raw, "daily_bar", None)
        prov = Provenance.for_payload(
            provider="alpaca_data",
            source_url=f"{_ALPACA_DATA_BASE}/{symbol}/snapshot",
            payload=_to_jsonable(raw),
            request_key=f"snapshot:{symbol}",
        )
        return Snapshot(
            symbol=symbol,
            last_price=_decimal(getattr(latest_trade, "price", None)),
            last_trade_at=getattr(latest_trade, "timestamp", None),
            daily_volume=_int(getattr(daily_bar, "volume", None)),
            provenance=prov,
        )

    def get_latest_quote(self, symbol: str) -> Quote:
        from alpaca.data.requests import StockLatestQuoteRequest

        symbol = symbol.upper()
        req = StockLatestQuoteRequest(symbol_or_symbols=symbol)
        result = self._client.get_stock_latest_quote(req)
        raw = _select(result, symbol)
        prov = Provenance.for_payload(
            provider="alpaca_data",
            source_url=f"{_ALPACA_DATA_BASE}/{symbol}/quotes/latest",
            payload=_to_jsonable(raw),
            request_key=f"latest_quote:{symbol}",
        )
        return Quote(
            symbol=symbol,
            bid_price=_decimal(getattr(raw, "bid_price", None)) or Decimal("0"),
            ask_price=_decimal(getattr(raw, "ask_price", None)) or Decimal("0"),
            timestamp=getattr(raw, "timestamp", _utcnow()),
            provenance=prov,
        )

    # ------------------------------------------------------------------
    # Bars + ADV
    # ------------------------------------------------------------------
    def get_daily_bars(self, symbol: str, *, days: int) -> tuple[tuple[Bar, ...], Provenance]:
        from alpaca.data.requests import StockBarsRequest
        from alpaca.data.timeframe import TimeFrame

        if days <= 0:
            raise ValueError("days must be positive")
        symbol = symbol.upper()
        end = _utcnow()
        # Add a small buffer so weekends/holidays don't shrink the window.
        start = end - dt.timedelta(days=int(days * 1.6) + 2)
        req = StockBarsRequest(
            symbol_or_symbols=symbol,
            timeframe=TimeFrame.Day,
            start=start,
            end=end,
        )
        result = self._client.get_stock_bars(req)
        bars_raw = _bars_from_result(result, symbol)
        bars = tuple(
            Bar(
                timestamp=b.timestamp,
                open=Decimal(str(b.open)),
                high=Decimal(str(b.high)),
                low=Decimal(str(b.low)),
                close=Decimal(str(b.close)),
                volume=int(b.volume or 0),
            )
            for b in bars_raw
        )[-days:]
        prov = Provenance.for_payload(
            provider="alpaca_data",
            source_url=f"{_ALPACA_DATA_BASE}/{symbol}/bars/day",
            payload=[_to_jsonable(b) for b in bars_raw],
            request_key=f"daily_bars:{symbol}:{days}d",
        )
        return bars, prov

    def compute_adv_usd(self, symbol: str, *, days: int = 20) -> AdvResult:
        bars, prov = self.get_daily_bars(symbol, days=days)
        warnings: list[str] = []
        if len(bars) < max(5, days // 2):
            warnings.append("insufficient_history")
        if bars:
            adv = sum((b.dollar_volume for b in bars), start=Decimal("0")) / Decimal(len(bars))
        else:
            adv = Decimal("0")
            warnings.append("no_bars")
        if warnings:
            prov = prov.model_copy(update={"data_quality_warnings": tuple(warnings)})
        return AdvResult(
            symbol=symbol.upper(),
            window_days=days,
            adv_usd=adv.quantize(Decimal("0.01")),
            bars_used=len(bars),
            provenance=prov,
        )


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def _select(result: Any, symbol: str) -> Any:
    """alpaca-py returns a dict for batch calls; normalise to a single object."""

    if isinstance(result, dict):
        return result.get(symbol) or result.get(symbol.upper()) or next(iter(result.values()))
    return result


def _bars_from_result(result: Any, symbol: str) -> list[Any]:
    if isinstance(result, dict):
        return list(result.get(symbol) or result.get(symbol.upper()) or [])
    data = getattr(result, "data", None)
    if isinstance(data, dict):
        return list(data.get(symbol) or data.get(symbol.upper()) or [])
    return list(data or [])


def _decimal(value: Any) -> Decimal | None:
    if value is None:
        return None
    try:
        return Decimal(str(value))
    except (ValueError, ArithmeticError):
        return None


def _int(value: Any) -> int | None:
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _to_jsonable(obj: Any) -> Any:
    """Best-effort conversion of alpaca-py model objects to JSON-friendly dicts."""

    if obj is None or isinstance(obj, (str, int, float, bool)):
        return obj
    if isinstance(obj, (list, tuple)):
        return [_to_jsonable(x) for x in obj]
    if isinstance(obj, dict):
        return {str(k): _to_jsonable(v) for k, v in obj.items()}
    if hasattr(obj, "model_dump"):
        return obj.model_dump(mode="json")
    if hasattr(obj, "__dict__"):
        return {k: _to_jsonable(v) for k, v in vars(obj).items() if not k.startswith("_")}
    return str(obj)


__all__ = (
    "AdvResult",
    "Bar",
    "MarketDataAdapter",
    "Quote",
    "Snapshot",
)
