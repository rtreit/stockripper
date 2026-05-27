"""Paper-only Alpaca data + news client factories.

Order-capable clients are deliberately *not* exposed here so research code
cannot construct them. Phase 5's execution adapter owns the
:class:`alpaca.trading.client.TradingClient` factory in a different module.
"""

from __future__ import annotations

from typing import Any, Protocol

from stockripper.config import StockripperSettings, load_settings


class StockDataLike(Protocol):
    """Minimal protocol the market-data adapter relies on.

    Defined as a protocol so unit tests can drop in a fake without depending
    on alpaca-py being installed in the test environment.
    """

    def get_stock_snapshot(self, request: Any) -> Any: ...
    def get_stock_bars(self, request: Any) -> Any: ...
    def get_stock_latest_quote(self, request: Any) -> Any: ...


class NewsClientLike(Protocol):
    def get_news(self, request: Any) -> Any: ...


class TradingClientLike(Protocol):
    """Read-only trading-client surface exposed for ``get_all_assets``."""

    def get_all_assets(self, filter: Any | None = None) -> Any: ...


def _credentials(settings: StockripperSettings | None) -> tuple[str, str]:
    cfg = settings if settings is not None else load_settings()
    cfg.assert_paper_only()
    return (
        cfg.alpaca_api_key_id.get_secret_value(),
        cfg.alpaca_api_secret_key.get_secret_value(),
    )


def build_stock_data_client(
    settings: StockripperSettings | None = None,
) -> StockDataLike:
    """Return an alpaca-py historical stock-data client."""

    from alpaca.data.historical.stock import StockHistoricalDataClient

    key_id, secret = _credentials(settings)
    return StockHistoricalDataClient(api_key=key_id, secret_key=secret)


def build_news_client(
    settings: StockripperSettings | None = None,
) -> NewsClientLike:
    """Return an alpaca-py news client."""

    from alpaca.data.historical.news import NewsClient

    key_id, secret = _credentials(settings)
    return NewsClient(api_key=key_id, secret_key=secret)


def build_paper_reference_client(
    settings: StockripperSettings | None = None,
) -> TradingClientLike:
    """Return a *reference-only* paper trading client.

    Yes, this is technically ``alpaca.trading.client.TradingClient`` — but
    we expose it via the :class:`TradingClientLike` protocol that only
    advertises ``get_all_assets``, and the function name signals intent.
    The Phase 2 universe builder needs the tradable-asset list; nothing
    else.

    For order submission, use the execution-adapter factory in Phase 5,
    *not* this function.
    """

    from alpaca.trading.client import TradingClient

    key_id, secret = _credentials(settings)
    return TradingClient(api_key=key_id, secret_key=secret, paper=True)


__all__ = (
    "NewsClientLike",
    "StockDataLike",
    "TradingClientLike",
    "build_news_client",
    "build_paper_reference_client",
    "build_stock_data_client",
)
