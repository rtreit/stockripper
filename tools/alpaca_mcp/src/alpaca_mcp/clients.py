"""Lazy, cached factory for the alpaca-py SDK clients.

Clients are constructed once per server process from the validated settings,
so each tool invocation just reads them. All clients are HTTP-only here —
the MCP server does not currently expose websocket streams.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache

from alpaca.data.historical.news import NewsClient
from alpaca.data.historical.option import OptionHistoricalDataClient
from alpaca.data.historical.stock import StockHistoricalDataClient
from alpaca.trading.client import TradingClient

from alpaca_mcp.config import AlpacaMcpSettings, load_settings


@dataclass(frozen=True)
class AlpacaClients:
    """All HTTP clients the MCP server needs, bundled for easy injection."""

    settings: AlpacaMcpSettings
    trading: TradingClient
    stock_data: StockHistoricalDataClient
    option_data: OptionHistoricalDataClient
    news: NewsClient


def build_clients(settings: AlpacaMcpSettings) -> AlpacaClients:
    key_id = settings.alpaca_api_key_id.get_secret_value()
    secret = settings.alpaca_api_secret_key.get_secret_value()
    paper = settings.is_paper
    return AlpacaClients(
        settings=settings,
        trading=TradingClient(api_key=key_id, secret_key=secret, paper=paper),
        stock_data=StockHistoricalDataClient(api_key=key_id, secret_key=secret),
        option_data=OptionHistoricalDataClient(api_key=key_id, secret_key=secret),
        news=NewsClient(api_key=key_id, secret_key=secret),
    )


@lru_cache(maxsize=1)
def get_clients() -> AlpacaClients:
    """Process-wide cached clients. Construct once per server lifetime."""

    return build_clients(load_settings())


__all__ = ("AlpacaClients", "build_clients", "get_clients")
