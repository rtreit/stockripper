"""Live wiring: connects the pluggable :class:`UniverseBuilder` to Alpaca.

Pulled into its own module so unit tests of the builder don't accidentally
import alpaca-py and so the builder remains driveable from fakes.
"""

from __future__ import annotations

import datetime as dt
from collections.abc import Iterable, Mapping

from stockripper.config import StockripperSettings
from stockripper.data.market_data import MarketDataAdapter
from stockripper.data.news import NewsAdapter
from stockripper.data.universe import AssetRecord, AssetSnapshot

# These tickers / fragments are heuristic — leveraged ETF metadata isn't
# available from the Alpaca asset list directly, so we tag a small allow-list
# of obvious examples. Fully populating this is a Phase 3 problem.
_LEVERAGED_ETF_HINTS: tuple[str, ...] = (
    "TQQQ", "SQQQ", "SPXL", "SPXS", "SOXL", "SOXS", "TNA", "TZA",
    "UPRO", "SPXU", "FAZ", "FAS", "UVXY", "SVXY", "LABU", "LABD",
)


class AlpacaAssetsLoader:
    """Callable that returns the tradable equity universe from Alpaca."""

    def __init__(self, *, settings: StockripperSettings | None = None) -> None:
        from stockripper.integrations.alpaca import build_paper_reference_client

        self._client = build_paper_reference_client(settings)

    def __call__(self) -> Iterable[AssetRecord]:
        from alpaca.trading.enums import AssetClass, AssetStatus
        from alpaca.trading.requests import GetAssetsRequest

        req = GetAssetsRequest(asset_class=AssetClass.US_EQUITY, status=AssetStatus.ACTIVE)
        for asset in self._client.get_all_assets(req):
            symbol = getattr(asset, "symbol", None)
            if not symbol:
                continue
            is_etf = bool(
                getattr(asset, "attributes", None)
                and "etf" in {str(a).lower() for a in asset.attributes}
            )
            yield AssetRecord(
                symbol=str(symbol).upper(),
                name=str(getattr(asset, "name", "") or ""),
                exchange=str(getattr(asset, "exchange", "") or ""),
                tradable=bool(getattr(asset, "tradable", False)),
                shortable=bool(getattr(asset, "shortable", False)),
                fractionable=bool(getattr(asset, "fractionable", False)),
                is_etf=is_etf,
                is_leveraged_etf=str(symbol).upper() in _LEVERAGED_ETF_HINTS,
            )


class AlpacaSnapshotProvider:
    """Bulk snapshot provider backed by Alpaca data + news clients.

    Phase-2 strategy: we don't fetch market-cap from Alpaca (it's not in the
    snapshot endpoint) — we leave ``market_cap_usd=None`` and let the
    universe filter reject anything that doesn't have a band when the
    track's policy requires one. Phase 3 will batch SEC EDGAR
    shares-outstanding lookups to populate this.
    """

    def __init__(
        self,
        *,
        settings: StockripperSettings | None = None,
        market: MarketDataAdapter | None = None,
        news: NewsAdapter | None = None,
        adv_days: int = 20,
    ) -> None:
        self._market = market if market is not None else MarketDataAdapter()
        self._news = news if news is not None else NewsAdapter()
        self._adv_days = adv_days

    def get_snapshots(
        self, symbols: Iterable[str], *, as_of: dt.date,
    ) -> Mapping[str, AssetSnapshot]:
        out: dict[str, AssetSnapshot] = {}
        for symbol in symbols:
            symbol = symbol.upper()
            try:
                snap = self._market.get_snapshot(symbol)
                adv = self._market.compute_adv_usd(symbol, days=self._adv_days)
            except Exception:
                continue
            if snap.last_price is None:
                continue
            out[symbol] = AssetSnapshot(
                symbol=symbol,
                last_price=snap.last_price,
                adv_usd_20d=adv.adv_usd,
                market_cap_usd=None,
                recent_8k_within_days=None,
                recent_news_count_30d=None,
            )
        return out


__all__ = ("AlpacaAssetsLoader", "AlpacaSnapshotProvider")
