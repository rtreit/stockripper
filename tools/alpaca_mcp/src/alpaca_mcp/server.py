"""MCP server exposing Alpaca trading + market data APIs.

The server uses ``FastMCP`` from the official MCP Python SDK and the
``alpaca-py`` SDK. Tool functions are intentionally thin wrappers that
construct typed request models, dispatch them, and serialise the response.

Run with::

    uv run alpaca-mcp           # via the console script
    python -m alpaca_mcp        # equivalent
"""

from __future__ import annotations

import sys
from typing import Any

from alpaca.common.enums import Sort
from alpaca.data.enums import Adjustment
from alpaca.data.requests import (
    NewsRequest,
    OptionBarsRequest,
    OptionChainRequest,
    OptionLatestQuoteRequest,
    OptionSnapshotRequest,
    StockBarsRequest,
    StockLatestQuoteRequest,
    StockLatestTradeRequest,
    StockSnapshotRequest,
)
from alpaca.data.timeframe import TimeFrame, TimeFrameUnit
from alpaca.trading.enums import (
    AssetClass,
    AssetExchange,
    AssetStatus,
    ContractType,
    OrderClass,
    OrderSide,
    OrderType,
    QueryOrderStatus,
    TimeInForce,
)
from alpaca.trading.requests import (
    ClosePositionRequest,
    GetAssetsRequest,
    GetCalendarRequest,
    GetOptionContractsRequest,
    GetOrdersRequest,
    GetPortfolioHistoryRequest,
    LimitOrderRequest,
    MarketOrderRequest,
    ReplaceOrderRequest,
    StopLimitOrderRequest,
    StopOrderRequest,
)
from mcp.server.fastmcp import FastMCP

from alpaca_mcp._serde import parse_date, parse_dt, to_jsonable
from alpaca_mcp.clients import get_clients


def _timeframe(value: str) -> TimeFrame:
    """Parse the wire-friendly timeframe string used by the tools.

    Accepts shorthand like ``1Min``, ``5Min``, ``15Min``, ``1Hour``, ``1Day``,
    ``1Week``, ``1Month``. Falls back to ``1Day`` on unknown input rather than
    raising so the agent's typos don't kill long-running flows.
    """

    table: dict[str, TimeFrame] = {
        "1min": TimeFrame(1, TimeFrameUnit.Minute),
        "5min": TimeFrame(5, TimeFrameUnit.Minute),
        "15min": TimeFrame(15, TimeFrameUnit.Minute),
        "30min": TimeFrame(30, TimeFrameUnit.Minute),
        "1hour": TimeFrame(1, TimeFrameUnit.Hour),
        "1day": TimeFrame(1, TimeFrameUnit.Day),
        "1week": TimeFrame(1, TimeFrameUnit.Week),
        "1month": TimeFrame(1, TimeFrameUnit.Month),
    }
    return table.get(value.lower(), TimeFrame(1, TimeFrameUnit.Day))


def _split_symbols(symbols: str | list[str]) -> list[str]:
    if isinstance(symbols, list):
        return [s.strip().upper() for s in symbols if s.strip()]
    return [s.strip().upper() for s in symbols.split(",") if s.strip()]


def build_server() -> FastMCP:
    """Build and register all tools on a fresh FastMCP instance."""

    mcp = FastMCP("alpaca")

    # ----------------------------------------------------------------------
    # Account / clock / calendar
    # ----------------------------------------------------------------------
    @mcp.tool()
    def get_account() -> dict[str, Any]:
        """Return the Alpaca account summary (equity, buying power, status...)."""

        c = get_clients()
        return to_jsonable(c.trading.get_account())

    @mcp.tool()
    def get_clock() -> dict[str, Any]:
        """Return current market clock info (timestamp, is_open, next_open/close)."""

        c = get_clients()
        return to_jsonable(c.trading.get_clock())

    @mcp.tool()
    def get_calendar(start: str | None = None, end: str | None = None) -> list[dict[str, Any]]:
        """Return the trading calendar between ``start`` and ``end`` (ISO dates)."""

        c = get_clients()
        req = GetCalendarRequest(start=parse_date(start), end=parse_date(end))
        return to_jsonable(c.trading.get_calendar(filters=req))

    # ----------------------------------------------------------------------
    # Positions
    # ----------------------------------------------------------------------
    @mcp.tool()
    def get_positions() -> list[dict[str, Any]]:
        """Return all currently open positions."""

        c = get_clients()
        return to_jsonable(c.trading.get_all_positions())

    @mcp.tool()
    def get_position(symbol_or_asset_id: str) -> dict[str, Any]:
        """Return a single open position by symbol or asset id."""

        c = get_clients()
        return to_jsonable(c.trading.get_open_position(symbol_or_asset_id))

    @mcp.tool()
    def close_position(
        symbol_or_asset_id: str,
        qty: str | None = None,
        percentage: str | None = None,
    ) -> dict[str, Any]:
        """Close a position fully or partially by qty or percentage."""

        c = get_clients()
        req = ClosePositionRequest(qty=qty, percentage=percentage) if (qty or percentage) else None
        return to_jsonable(c.trading.close_position(symbol_or_asset_id, close_options=req))

    @mcp.tool()
    def close_all_positions(cancel_orders: bool = True) -> list[dict[str, Any]]:
        """Close every open position; optionally cancel all open orders first."""

        c = get_clients()
        return to_jsonable(c.trading.close_all_positions(cancel_orders=cancel_orders))

    # ----------------------------------------------------------------------
    # Orders
    # ----------------------------------------------------------------------
    @mcp.tool()
    def get_orders(
        status: str = "open",
        limit: int = 50,
        after: str | None = None,
        until: str | None = None,
        direction: str = "desc",
        symbols: str | list[str] | None = None,
        nested: bool = False,
    ) -> list[dict[str, Any]]:
        """List orders. ``status`` is one of open|closed|all."""

        c = get_clients()
        sym = _split_symbols(symbols) if symbols else None
        req = GetOrdersRequest(
            status=QueryOrderStatus(status.lower()),
            limit=limit,
            after=parse_dt(after),
            until=parse_dt(until),
            direction=Sort(direction.lower()),
            symbols=sym,
            nested=nested,
        )
        return to_jsonable(c.trading.get_orders(filter=req))

    @mcp.tool()
    def get_order(order_id: str) -> dict[str, Any]:
        """Return a single order by id."""

        c = get_clients()
        return to_jsonable(c.trading.get_order_by_id(order_id))

    @mcp.tool()
    def submit_order(
        symbol: str,
        side: str,
        type: str = "market",
        time_in_force: str = "day",
        qty: float | None = None,
        notional: float | None = None,
        limit_price: float | None = None,
        stop_price: float | None = None,
        trail_price: float | None = None,
        trail_percent: float | None = None,
        extended_hours: bool = False,
        client_order_id: str | None = None,
        order_class: str | None = None,
    ) -> dict[str, Any]:
        """Submit an order. Exactly one of ``qty`` or ``notional`` must be given."""

        if (qty is None) == (notional is None):
            raise ValueError("Provide exactly one of qty or notional.")

        c = get_clients()
        side_enum = OrderSide(side.lower())
        tif_enum = TimeInForce(time_in_force.lower())
        oclass = OrderClass(order_class.lower()) if order_class else None
        otype = OrderType(type.lower())

        common: dict[str, Any] = {
            "symbol": symbol.upper(),
            "side": side_enum,
            "time_in_force": tif_enum,
            "extended_hours": extended_hours,
            "client_order_id": client_order_id,
            "order_class": oclass,
        }
        if qty is not None:
            common["qty"] = qty
        if notional is not None:
            common["notional"] = notional

        req: Any
        if otype is OrderType.MARKET:
            req = MarketOrderRequest(**common)
        elif otype is OrderType.LIMIT:
            if limit_price is None:
                raise ValueError("limit_price is required for limit orders.")
            req = LimitOrderRequest(limit_price=limit_price, **common)
        elif otype is OrderType.STOP:
            if stop_price is None:
                raise ValueError("stop_price is required for stop orders.")
            req = StopOrderRequest(stop_price=stop_price, **common)
        elif otype is OrderType.STOP_LIMIT:
            if limit_price is None or stop_price is None:
                raise ValueError("limit_price and stop_price are required for stop-limit orders.")
            req = StopLimitOrderRequest(
                limit_price=limit_price, stop_price=stop_price, **common
            )
        else:
            raise ValueError(f"Unsupported order type: {type!r}")

        # Suppress unused-arg warnings for trailing-stop placeholders; full
        # trailing-stop support lands when we add the corresponding branch.
        _ = (trail_price, trail_percent)

        return to_jsonable(c.trading.submit_order(order_data=req))

    @mcp.tool()
    def replace_order(
        order_id: str,
        qty: int | None = None,
        time_in_force: str | None = None,
        limit_price: float | None = None,
        stop_price: float | None = None,
        trail: float | None = None,
        client_order_id: str | None = None,
    ) -> dict[str, Any]:
        """Replace (modify) an open order in place."""

        c = get_clients()
        req = ReplaceOrderRequest(
            qty=qty,
            time_in_force=TimeInForce(time_in_force.lower()) if time_in_force else None,
            limit_price=limit_price,
            stop_price=stop_price,
            trail=trail,
            client_order_id=client_order_id,
        )
        return to_jsonable(c.trading.replace_order_by_id(order_id, order_data=req))

    @mcp.tool()
    def cancel_order(order_id: str) -> dict[str, Any]:
        """Cancel a single order by id."""

        c = get_clients()
        c.trading.cancel_order_by_id(order_id)
        return {"order_id": order_id, "cancel_requested": True}

    @mcp.tool()
    def cancel_all_orders() -> list[dict[str, Any]]:
        """Cancel every open order. Returns the per-order cancel status list."""

        c = get_clients()
        return to_jsonable(c.trading.cancel_orders())

    # ----------------------------------------------------------------------
    # Portfolio / assets
    # ----------------------------------------------------------------------
    @mcp.tool()
    def get_portfolio_history(
        period: str | None = None,
        timeframe: str | None = None,
        intraday_reporting: str | None = None,
        start: str | None = None,
        end: str | None = None,
        pnl_reset: str | None = None,
    ) -> dict[str, Any]:
        """Return the account's portfolio equity history over the given window."""

        c = get_clients()
        req = GetPortfolioHistoryRequest(
            period=period,
            timeframe=timeframe,
            intraday_reporting=intraday_reporting,
            start=parse_dt(start),
            end=parse_dt(end),
            pnl_reset=pnl_reset,
        )
        return to_jsonable(c.trading.get_portfolio_history(history_filter=req))

    @mcp.tool()
    def get_assets(
        status: str = "active",
        asset_class: str = "us_equity",
        exchange: str | None = None,
        attributes: str | None = None,
    ) -> list[dict[str, Any]]:
        """Return the master asset list, filtered by status / class / exchange."""

        c = get_clients()
        req = GetAssetsRequest(
            status=AssetStatus(status.lower()),
            asset_class=AssetClass(asset_class.lower()),
            exchange=AssetExchange(exchange.upper()) if exchange else None,
            attributes=attributes,
        )
        return to_jsonable(c.trading.get_all_assets(filter=req))

    @mcp.tool()
    def get_asset(symbol_or_asset_id: str) -> dict[str, Any]:
        """Return a single asset's metadata."""

        c = get_clients()
        return to_jsonable(c.trading.get_asset(symbol_or_asset_id))

    # ----------------------------------------------------------------------
    # Stock market data
    # ----------------------------------------------------------------------
    @mcp.tool()
    def get_stock_bars(
        symbols: str | list[str],
        timeframe: str = "1Day",
        start: str | None = None,
        end: str | None = None,
        limit: int | None = None,
        adjustment: str | None = None,
    ) -> dict[str, Any]:
        """Return historical OHLCV bars for one or more symbols."""

        c = get_clients()
        req = StockBarsRequest(
            symbol_or_symbols=_split_symbols(symbols),
            timeframe=_timeframe(timeframe),
            start=parse_dt(start),
            end=parse_dt(end),
            limit=limit,
            adjustment=Adjustment(adjustment.lower()) if adjustment else None,
        )
        result = c.stock_data.get_stock_bars(req)
        return to_jsonable(getattr(result, "data", result))

    @mcp.tool()
    def get_stock_latest_quote(symbols: str | list[str]) -> dict[str, Any]:
        """Return the latest NBBO quote for each requested symbol."""

        c = get_clients()
        req = StockLatestQuoteRequest(symbol_or_symbols=_split_symbols(symbols))
        return to_jsonable(c.stock_data.get_stock_latest_quote(req))

    @mcp.tool()
    def get_stock_latest_trade(symbols: str | list[str]) -> dict[str, Any]:
        """Return the latest trade for each requested symbol."""

        c = get_clients()
        req = StockLatestTradeRequest(symbol_or_symbols=_split_symbols(symbols))
        return to_jsonable(c.stock_data.get_stock_latest_trade(req))

    @mcp.tool()
    def get_stock_snapshot(symbols: str | list[str]) -> dict[str, Any]:
        """Return latest quote, latest trade, and prior/today bars per symbol."""

        c = get_clients()
        req = StockSnapshotRequest(symbol_or_symbols=_split_symbols(symbols))
        return to_jsonable(c.stock_data.get_stock_snapshot(req))

    # ----------------------------------------------------------------------
    # News
    # ----------------------------------------------------------------------
    @mcp.tool()
    def get_news(
        symbols: str | list[str] | None = None,
        start: str | None = None,
        end: str | None = None,
        limit: int = 50,
        include_content: bool = False,
        sort: str = "desc",
    ) -> list[dict[str, Any]]:
        """Return Alpaca News API articles, optionally filtered by symbols."""

        c = get_clients()
        sym_list = _split_symbols(symbols) if symbols else None
        req = NewsRequest(
            symbols=",".join(sym_list) if sym_list else None,
            start=parse_dt(start),
            end=parse_dt(end),
            limit=limit,
            include_content=include_content,
            sort=Sort(sort.lower()),
        )
        result = c.news.get_news(req)
        return to_jsonable(getattr(result, "data", result))

    # ----------------------------------------------------------------------
    # Options
    # ----------------------------------------------------------------------
    @mcp.tool()
    def get_option_contracts(
        underlying_symbols: str | list[str] | None = None,
        status: str = "active",
        expiration_date: str | None = None,
        expiration_date_gte: str | None = None,
        expiration_date_lte: str | None = None,
        strike_price_gte: str | None = None,
        strike_price_lte: str | None = None,
        type: str | None = None,
        limit: int = 100,
    ) -> dict[str, Any]:
        """List option contracts on Alpaca matching the filter."""

        c = get_clients()
        req = GetOptionContractsRequest(
            underlying_symbols=_split_symbols(underlying_symbols)
            if underlying_symbols
            else None,
            status=AssetStatus(status.lower()),
            expiration_date=parse_date(expiration_date),
            expiration_date_gte=parse_date(expiration_date_gte),
            expiration_date_lte=parse_date(expiration_date_lte),
            strike_price_gte=strike_price_gte,
            strike_price_lte=strike_price_lte,
            type=ContractType(type.lower()) if type else None,
            limit=limit,
        )
        return to_jsonable(c.trading.get_option_contracts(req))

    @mcp.tool()
    def get_option_chain(
        underlying_symbol: str, expiration_date: str | None = None
    ) -> dict[str, Any]:
        """Return the full option chain snapshot for an underlying."""

        c = get_clients()
        req = OptionChainRequest(
            underlying_symbol=underlying_symbol.upper(),
            expiration_date=parse_date(expiration_date),
        )
        return to_jsonable(c.option_data.get_option_chain(req))

    @mcp.tool()
    def get_option_snapshot(symbols: str | list[str]) -> dict[str, Any]:
        """Return latest snapshots for one or more option contract symbols (OCC)."""

        c = get_clients()
        req = OptionSnapshotRequest(symbol_or_symbols=_split_symbols(symbols))
        return to_jsonable(c.option_data.get_option_snapshot(req))

    @mcp.tool()
    def get_option_latest_quote(symbols: str | list[str]) -> dict[str, Any]:
        """Return the latest quote for one or more option contract symbols."""

        c = get_clients()
        req = OptionLatestQuoteRequest(symbol_or_symbols=_split_symbols(symbols))
        return to_jsonable(c.option_data.get_option_latest_quote(req))

    @mcp.tool()
    def get_option_bars(
        symbols: str | list[str],
        timeframe: str = "1Day",
        start: str | None = None,
        end: str | None = None,
        limit: int | None = None,
    ) -> dict[str, Any]:
        """Return historical bars for option contract symbols (OCC)."""

        c = get_clients()
        req = OptionBarsRequest(
            symbol_or_symbols=_split_symbols(symbols),
            timeframe=_timeframe(timeframe),
            start=parse_dt(start),
            end=parse_dt(end),
            limit=limit,
        )
        result = c.option_data.get_option_bars(req)
        return to_jsonable(getattr(result, "data", result))

    # ----------------------------------------------------------------------
    # Server-level diagnostic
    # ----------------------------------------------------------------------
    @mcp.tool()
    def alpaca_mode() -> dict[str, Any]:
        """Report which mode (paper or live) the server is running in."""

        c = get_clients()
        return {
            "mode": c.settings.alpaca_mode.value,
            "trading_base_url": c.settings.trading_base_url,
            "is_paper": c.settings.is_paper,
        }

    return mcp


def run() -> None:
    """Entry point used by ``python -m alpaca_mcp`` and the console script."""

    # Import-time settings load surfaces config errors loudly before the
    # MCP stdio loop swallows stderr.
    from alpaca_mcp.config import load_settings

    settings = load_settings()
    print(settings.banner(), file=sys.stderr, flush=True)
    server = build_server()
    server.run()


__all__ = ("build_server", "run")
