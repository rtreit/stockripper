"""Smoke tests for the MCP server tool wiring.

These don't hit the network. They verify the FastMCP server exposes the
expected tool names and that the serde helpers handle alpaca-py-style
objects correctly.
"""

from __future__ import annotations

from datetime import UTC, date, datetime
from typing import Any

import pytest
from pydantic import BaseModel

from alpaca_mcp._serde import parse_date, parse_dt, to_jsonable

EXPECTED_TOOLS = {
    # account / clock / calendar
    "get_account",
    "get_clock",
    "get_calendar",
    # positions
    "get_positions",
    "get_position",
    "close_position",
    "close_all_positions",
    # orders
    "get_orders",
    "get_order",
    "submit_order",
    "replace_order",
    "cancel_order",
    "cancel_all_orders",
    # portfolio / assets
    "get_portfolio_history",
    "get_assets",
    "get_asset",
    # stock market data
    "get_stock_bars",
    "get_stock_latest_quote",
    "get_stock_latest_trade",
    "get_stock_snapshot",
    # news
    "get_news",
    # options
    "get_option_contracts",
    "get_option_chain",
    "get_option_snapshot",
    "get_option_latest_quote",
    "get_option_bars",
    # diagnostic
    "alpaca_mode",
}


@pytest.mark.asyncio
async def test_server_exposes_expected_tools(paper_creds: None) -> None:
    from alpaca_mcp.server import build_server

    server = build_server()
    tools = await server.list_tools()
    names = {t.name for t in tools}
    missing = EXPECTED_TOOLS - names
    assert not missing, f"Missing tools: {sorted(missing)}"


def test_parse_dt_roundtrip() -> None:
    assert parse_dt(None) is None
    parsed = parse_dt("2026-05-26T14:00:00+00:00")
    assert parsed == datetime(2026, 5, 26, 14, 0, tzinfo=UTC)
    assert parse_dt(parsed) is parsed


def test_parse_date_roundtrip() -> None:
    assert parse_date(None) is None
    assert parse_date("2026-05-26") == date(2026, 5, 26)


class _Sample(BaseModel):
    symbol: str
    qty: float
    when: datetime


def test_to_jsonable_handles_pydantic_models() -> None:
    obj = _Sample(symbol="AAPL", qty=10, when=datetime(2026, 5, 26, 14, tzinfo=UTC))
    out: Any = to_jsonable(obj)
    assert isinstance(out, dict)
    assert out["symbol"] == "AAPL"
    assert out["qty"] == 10
    assert "2026-05-26" in out["when"]


def test_to_jsonable_handles_nested_collections() -> None:
    raw = {
        "items": [
            _Sample(symbol="A", qty=1, when=datetime(2026, 1, 1, tzinfo=UTC)),
            _Sample(symbol="B", qty=2, when=datetime(2026, 1, 2, tzinfo=UTC)),
        ],
        "count": 2,
    }
    out: Any = to_jsonable(raw)
    assert out["count"] == 2
    assert [r["symbol"] for r in out["items"]] == ["A", "B"]
