"""Tests for :class:`NewsAdapter`."""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from typing import Any

from stockripper.data.news import NewsAdapter


@dataclass
class _FakeRawNews:
    id: int
    headline: str
    summary: str
    author: str
    url: str
    symbols: list[str]
    created_at: dt.datetime
    updated_at: dt.datetime
    source: str


class _FakeNewsClient:
    def __init__(self, items: list[_FakeRawNews]) -> None:
        self._items = items
        self.calls: list[Any] = []

    def get_news(self, request: Any) -> list[_FakeRawNews]:
        self.calls.append(request)
        return list(self._items)


def _items(n: int) -> list[_FakeRawNews]:
    base = dt.datetime.now(dt.UTC) - dt.timedelta(days=1)
    return [
        _FakeRawNews(
            id=i,
            headline=f"Headline {i}",
            summary=f"Summary {i}",
            author="Reporter",
            url=f"https://example.com/{i}",
            symbols=["AAPL"],
            created_at=base + dt.timedelta(minutes=i),
            updated_at=base + dt.timedelta(minutes=i),
            source="benzinga",
        )
        for i in range(n)
    ]


def test_get_recent_news_returns_provenance_tagged_items() -> None:
    fake = _FakeNewsClient(_items(3))
    adapter = NewsAdapter(client=fake)
    out = adapter.get_recent_news(["aapl"], since=dt.datetime.now(dt.UTC) - dt.timedelta(days=2))
    assert len(out) == 3
    for item in out:
        assert item.symbols == ("AAPL",)
        assert item.provenance.provider == "alpaca_news"


def test_count_recent_news_returns_int() -> None:
    fake = _FakeNewsClient(_items(7))
    adapter = NewsAdapter(client=fake)
    count = adapter.count_recent_news(
        "AAPL", since=dt.datetime.now(dt.UTC) - dt.timedelta(days=2)
    )
    assert count == 7


def test_get_recent_news_empty_symbols_short_circuits() -> None:
    fake = _FakeNewsClient(_items(5))
    adapter = NewsAdapter(client=fake)
    assert adapter.get_recent_news([]) == ()
    assert fake.calls == []
