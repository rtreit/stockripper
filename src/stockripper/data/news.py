"""Alpaca News API adapter.

Returns provenance-tagged :class:`NewsItem` records. The adapter is
intentionally read-only and non-order-capable.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from typing import Any

from stockripper.data.provenance import Provenance
from stockripper.integrations.alpaca import NewsClientLike, build_news_client

_ALPACA_NEWS_BASE: str = "alpaca-data://news"


@dataclass(frozen=True)
class NewsItem:
    id: str
    headline: str
    summary: str
    author: str | None
    url: str | None
    symbols: tuple[str, ...]
    created_at: dt.datetime
    updated_at: dt.datetime | None
    source: str | None
    provenance: Provenance


class NewsAdapter:
    """Thin wrapper around alpaca-py's NewsClient."""

    def __init__(self, client: NewsClientLike | None = None) -> None:
        self._client = client if client is not None else build_news_client()

    def get_recent_news(
        self,
        symbols: list[str] | tuple[str, ...],
        *,
        since: dt.datetime | None = None,
        limit: int = 50,
    ) -> tuple[NewsItem, ...]:
        from alpaca.data.requests import NewsRequest

        if not symbols:
            return ()
        upper_symbols = [s.upper() for s in symbols]
        kwargs: dict[str, Any] = {"symbols": ",".join(upper_symbols), "limit": limit}
        if since is not None:
            kwargs["start"] = since
        req = NewsRequest(**kwargs)
        result = self._client.get_news(req)
        raw_items = _items_from_result(result)
        out: list[NewsItem] = []
        for raw in raw_items:
            prov = Provenance.for_payload(
                provider="alpaca_news",
                source_url=f"{_ALPACA_NEWS_BASE}/{getattr(raw, 'id', '?')}",
                payload=_to_jsonable(raw),
                request_key=f"news:{','.join(upper_symbols)}:{since}:{limit}",
            )
            out.append(_to_news_item(raw, prov))
        return tuple(out)

    def count_recent_news(
        self,
        symbol: str,
        *,
        since: dt.datetime,
        limit: int = 100,
    ) -> int:
        """Used by the low-visibility filter to count items in a window."""

        return len(self.get_recent_news([symbol], since=since, limit=limit))


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def _items_from_result(result: Any) -> list[Any]:
    if isinstance(result, list):
        return result
    if isinstance(result, dict):
        # Alpaca returns {"news": [...]} for some surfaces.
        items = result.get("news") or result.get("data") or []
        if isinstance(items, dict):
            # Older shape: {"AAPL": [...]} — flatten.
            flat: list[Any] = []
            for v in items.values():
                if isinstance(v, list):
                    flat.extend(v)
            return flat
        return list(items)
    data = getattr(result, "data", None) or getattr(result, "news", None)
    if isinstance(data, dict):
        flat = []
        for v in data.values():
            if isinstance(v, list):
                flat.extend(v)
        return flat
    return list(data or [])


def _to_news_item(raw: Any, provenance: Provenance) -> NewsItem:
    symbols = getattr(raw, "symbols", None) or []
    return NewsItem(
        id=str(getattr(raw, "id", "")),
        headline=str(getattr(raw, "headline", "") or ""),
        summary=str(getattr(raw, "summary", "") or ""),
        author=_opt_str(getattr(raw, "author", None)),
        url=_opt_str(getattr(raw, "url", None)),
        symbols=tuple(str(s).upper() for s in symbols),
        created_at=getattr(raw, "created_at", None) or dt.datetime.now(dt.UTC),
        updated_at=getattr(raw, "updated_at", None),
        source=_opt_str(getattr(raw, "source", None)),
        provenance=provenance,
    )


def _opt_str(v: Any) -> str | None:
    if v is None:
        return None
    s = str(v)
    return s if s else None


def _to_jsonable(obj: Any) -> Any:
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


__all__ = ("NewsAdapter", "NewsItem")
