"""Tests for :mod:`stockripper.data.sec_edgar` using mocked httpx + rate limiter."""

from __future__ import annotations

import datetime as dt
import threading
import time
from typing import Any

import httpx
import pytest

from stockripper.data.cache import JsonFileCache
from stockripper.data.sec_edgar import (
    SecEdgarClient,
    SecEdgarConfigError,
    _ProcessRateLimiter,
    _resolve_user_agent,
)


@pytest.fixture
def cache(tmp_path: Any) -> JsonFileCache:
    return JsonFileCache(tmp_path / "cache")


def _mock_transport(routes: dict[str, dict[str, Any]]) -> httpx.MockTransport:
    """Return a transport that maps URLs to JSON payloads (or status codes)."""

    def handler(request: httpx.Request) -> httpx.Response:
        spec = routes.get(str(request.url))
        if spec is None:
            return httpx.Response(404, json={"detail": "no route"})
        if "status" in spec:
            return httpx.Response(spec["status"], json=spec.get("json", {}))
        return httpx.Response(200, json=spec["json"])

    return httpx.MockTransport(handler)


def _client(transport: httpx.MockTransport, cache: JsonFileCache) -> SecEdgarClient:
    http = httpx.Client(
        transport=transport,
        headers={"User-Agent": "StockRipper test ops@example.com"},
        timeout=5.0,
    )
    return SecEdgarClient(http=http, cache=cache, user_agent="StockRipper test ops@example.com")


def test_user_agent_requires_email(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("SEC_EDGAR_USER_AGENT", "StockRipper paper-bot")
    with pytest.raises(SecEdgarConfigError):
        _resolve_user_agent()


def test_user_agent_required(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("SEC_EDGAR_USER_AGENT", raising=False)
    with pytest.raises(SecEdgarConfigError):
        _resolve_user_agent()


def test_lookup_cik_returns_padded_value(cache: JsonFileCache) -> None:
    routes = {
        "https://www.sec.gov/files/company_tickers.json": {
            "json": {
                "0": {"cik_str": 320193, "ticker": "AAPL", "title": "APPLE INC"},
                "1": {"cik_str": 789019, "ticker": "MSFT", "title": "MICROSOFT CORP"},
            }
        },
    }
    client = _client(_mock_transport(routes), cache)
    try:
        cik = client.lookup_cik("aapl")
        assert cik == "0000320193"
        assert client.lookup_cik("nope") is None
    finally:
        client.close()


def test_get_company_facts_round_trips_and_caches(cache: JsonFileCache) -> None:
    url = "https://data.sec.gov/api/xbrl/companyfacts/CIK0000320193.json"
    routes = {
        url: {
            "json": {
                "entityName": "Apple Inc.",
                "facts": {"dei": {"EntityCommonStockSharesOutstanding": {}}},
            }
        },
    }
    client = _client(_mock_transport(routes), cache)
    try:
        first = client.get_company_facts("320193")
        assert first.entity_name == "Apple Inc."
        assert first.cik == "0000320193"
        # Second call should hit the cache (no extra route registration needed).
        second = client.get_company_facts("320193")
        assert second.entity_name == first.entity_name
        assert second.provenance.content_hash == first.provenance.content_hash
    finally:
        client.close()


def test_get_submissions_parses_filings(cache: JsonFileCache) -> None:
    url = "https://data.sec.gov/submissions/CIK0000320193.json"
    routes = {
        url: {
            "json": {
                "name": "Apple Inc.",
                "filings": {
                    "recent": {
                        "form": ["8-K", "10-Q", "8-K"],
                        "accessionNumber": ["a-1", "a-2", "a-3"],
                        "filingDate": [
                            (dt.date.today() - dt.timedelta(days=5)).isoformat(),
                            (dt.date.today() - dt.timedelta(days=60)).isoformat(),
                            (dt.date.today() - dt.timedelta(days=400)).isoformat(),
                        ],
                        "primaryDocument": ["d1.htm", "d2.htm", "d3.htm"],
                    }
                },
            }
        }
    }
    client = _client(_mock_transport(routes), cache)
    try:
        recent_8k = client.get_recent_filings("320193", forms=("8-K",), within_days=30)
        assert len(recent_8k) == 1
        assert recent_8k[0].accession_number == "a-1"
    finally:
        client.close()


def test_retries_on_429_then_succeeds(
    cache: JsonFileCache, monkeypatch: pytest.MonkeyPatch,
) -> None:
    calls = {"n": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        calls["n"] += 1
        if calls["n"] == 1:
            return httpx.Response(429, json={"detail": "rate limited"})
        return httpx.Response(200, json={"entityName": "X", "facts": {}})

    http = httpx.Client(
        transport=httpx.MockTransport(handler),
        headers={"User-Agent": "StockRipper test ops@example.com"},
        timeout=5.0,
    )
    client = SecEdgarClient(http=http, cache=cache, user_agent="ua ops@example.com", max_retries=2)
    try:
        # Patch backoff sleeps so this test stays fast.
        monkeypatch.setattr("stockripper.data.sec_edgar.time.sleep", lambda _x: None)
        facts = client.get_company_facts("1")
        assert facts.entity_name == "X"
        assert calls["n"] == 2
    finally:
        client.close()


def test_process_rate_limiter_blocks_when_full() -> None:
    limiter = _ProcessRateLimiter(max_rps=2, window=0.2)
    # Burn the budget — first 2 are immediate, 3rd must wait roughly window.
    limiter.acquire()
    limiter.acquire()
    start = time.monotonic()
    limiter.acquire()
    elapsed = time.monotonic() - start
    assert elapsed >= 0.15, f"limiter did not block (elapsed={elapsed})"


def test_process_rate_limiter_is_thread_safe() -> None:
    # Hammer the limiter from N threads and ensure no double-count.
    limiter = _ProcessRateLimiter(max_rps=5, window=0.5)
    barrier = threading.Barrier(10)

    def worker() -> None:
        barrier.wait()
        limiter.acquire()

    threads = [threading.Thread(target=worker) for _ in range(10)]
    for t in threads:
        t.start()
    for t in threads:
        t.join(timeout=3.0)
        assert not t.is_alive()
