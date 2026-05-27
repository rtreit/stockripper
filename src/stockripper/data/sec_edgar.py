"""SEC EDGAR HTTP adapter.

Honors SEC fair-access guidance:[^sec-edgar]

- Sends a meaningful ``User-Agent`` containing contact info (required).
- Process-wide ceiling of 10 requests/second across all clients (the SEC
  documented limit).
- Bounded retries with backoff on 429/5xx.

[^sec-edgar]: https://www.sec.gov/search-filings/edgar-application-programming-interfaces
"""

from __future__ import annotations

import datetime as dt
import os
import threading
import time
from collections import deque
from dataclasses import dataclass
from typing import Any, Final

import httpx

from stockripper.data.cache import JsonFileCache
from stockripper.data.provenance import Provenance

_EDGAR_SUBMISSIONS_BASE: Final[str] = "https://data.sec.gov/submissions"
_EDGAR_COMPANY_FACTS_BASE: Final[str] = "https://data.sec.gov/api/xbrl/companyfacts"
_EDGAR_TICKER_LOOKUP: Final[str] = "https://www.sec.gov/files/company_tickers.json"

_MAX_RPS: Final[int] = 10
_RATE_WINDOW: Final[float] = 1.0


class SecEdgarConfigError(RuntimeError):
    """Raised when SEC EDGAR access is misconfigured (e.g., missing User-Agent)."""


def _resolve_user_agent() -> str:
    """Return a non-empty SEC-compliant User-Agent string.

    SEC requires identifying contact information; a bare app name is
    insufficient. We enforce that the value contains an ``@`` so it's
    clearly a contact, not a placeholder.
    """

    ua = os.environ.get("SEC_EDGAR_USER_AGENT", "").strip()
    if not ua:
        raise SecEdgarConfigError(
            "SEC_EDGAR_USER_AGENT must be set to something like "
            "'StockRipper paper-research-bot ops@example.com' before calling EDGAR."
        )
    if "@" not in ua:
        raise SecEdgarConfigError(
            "SEC_EDGAR_USER_AGENT must include a contact email "
            "(SEC fair-access requirement)."
        )
    return ua


class _ProcessRateLimiter:
    """Sliding-window limiter — at most ``_MAX_RPS`` calls per second across the process."""

    def __init__(self, max_rps: int = _MAX_RPS, window: float = _RATE_WINDOW) -> None:
        self._max = max_rps
        self._window = window
        self._timestamps: deque[float] = deque()
        self._lock = threading.Lock()

    def acquire(self) -> None:
        while True:
            with self._lock:
                now = time.monotonic()
                while self._timestamps and now - self._timestamps[0] >= self._window:
                    self._timestamps.popleft()
                if len(self._timestamps) < self._max:
                    self._timestamps.append(now)
                    return
                sleep_for = self._window - (now - self._timestamps[0])
            time.sleep(max(sleep_for, 0.01))


_RATE_LIMITER: _ProcessRateLimiter = _ProcessRateLimiter()


# ---------------------------------------------------------------------------
# Dataclasses
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class Filing:
    accession_number: str
    form: str
    filing_date: dt.date
    primary_document: str | None


@dataclass(frozen=True)
class CompanyFacts:
    cik: str
    entity_name: str | None
    facts: dict[str, Any]
    provenance: Provenance


@dataclass(frozen=True)
class CompanySubmissions:
    cik: str
    entity_name: str | None
    recent_filings: tuple[Filing, ...]
    provenance: Provenance


# ---------------------------------------------------------------------------
# Client
# ---------------------------------------------------------------------------
class SecEdgarClient:
    """Cached httpx client for SEC EDGAR endpoints used in Phase 2."""

    def __init__(
        self,
        *,
        cache: JsonFileCache | None = None,
        http: httpx.Client | None = None,
        user_agent: str | None = None,
        max_retries: int = 3,
        ttl_company_facts: dt.timedelta = dt.timedelta(hours=12),
        ttl_submissions: dt.timedelta = dt.timedelta(hours=1),
        ttl_ticker_map: dt.timedelta = dt.timedelta(days=1),
    ) -> None:
        self._cache = cache if cache is not None else JsonFileCache()
        ua = user_agent if user_agent is not None else _resolve_user_agent()
        self._http = http if http is not None else httpx.Client(
            headers={"User-Agent": ua, "Accept": "application/json"},
            timeout=httpx.Timeout(15.0, connect=5.0),
        )
        self._max_retries = max_retries
        self._ttl_company_facts = ttl_company_facts
        self._ttl_submissions = ttl_submissions
        self._ttl_ticker_map = ttl_ticker_map

    def close(self) -> None:
        self._http.close()

    def __enter__(self) -> SecEdgarClient:
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    # ------------------------------------------------------------------
    # Ticker -> CIK
    # ------------------------------------------------------------------
    def lookup_cik(self, ticker: str) -> str | None:
        """Return the zero-padded 10-digit CIK for ``ticker`` or ``None``."""

        mapping = self._get_ticker_map()
        upper = ticker.upper()
        for entry in mapping.values():
            if isinstance(entry, dict) and str(entry.get("ticker", "")).upper() == upper:
                cik_int = int(entry["cik_str"])
                return f"{cik_int:010d}"
        return None

    def _get_ticker_map(self) -> dict[str, Any]:
        ns, key = "sec_edgar", "company_tickers"
        cached = self._cache.get(ns, key)
        if cached is not None:
            return dict(cached.value)
        payload = self._get_json(_EDGAR_TICKER_LOOKUP)
        self._cache.put(ns, key, payload, ttl=self._ttl_ticker_map)
        return dict(payload)

    # ------------------------------------------------------------------
    # Submissions (form list)
    # ------------------------------------------------------------------
    def get_submissions(self, cik: str) -> CompanySubmissions:
        cik = _normalise_cik(cik)
        ns, key = "sec_edgar", f"submissions_{cik}"
        cached = self._cache.get(ns, key)
        if cached is not None:
            payload = cached.value
        else:
            url = f"{_EDGAR_SUBMISSIONS_BASE}/CIK{cik}.json"
            payload = self._get_json(url)
            self._cache.put(ns, key, payload, ttl=self._ttl_submissions)
        recent = payload.get("filings", {}).get("recent", {}) or {}
        filings = _zip_filings(recent)
        prov = Provenance.for_payload(
            provider="sec_edgar",
            source_url=f"{_EDGAR_SUBMISSIONS_BASE}/CIK{cik}.json",
            payload=payload,
            request_key=key,
        )
        return CompanySubmissions(
            cik=cik,
            entity_name=payload.get("name"),
            recent_filings=tuple(filings),
            provenance=prov,
        )

    def get_recent_filings(
        self,
        cik: str,
        *,
        forms: tuple[str, ...] = ("8-K", "10-Q", "10-K", "S-1"),
        within_days: int | None = None,
    ) -> tuple[Filing, ...]:
        subs = self.get_submissions(cik)
        cutoff: dt.date | None = None
        if within_days is not None:
            cutoff = dt.date.today() - dt.timedelta(days=within_days)
        out: list[Filing] = []
        for f in subs.recent_filings:
            if forms and f.form not in forms:
                continue
            if cutoff is not None and f.filing_date < cutoff:
                continue
            out.append(f)
        return tuple(out)

    # ------------------------------------------------------------------
    # Company facts (XBRL)
    # ------------------------------------------------------------------
    def get_company_facts(self, cik: str) -> CompanyFacts:
        cik = _normalise_cik(cik)
        ns, key = "sec_edgar", f"company_facts_{cik}"
        cached = self._cache.get(ns, key)
        if cached is not None:
            payload = cached.value
        else:
            url = f"{_EDGAR_COMPANY_FACTS_BASE}/CIK{cik}.json"
            payload = self._get_json(url)
            self._cache.put(ns, key, payload, ttl=self._ttl_company_facts)
        prov = Provenance.for_payload(
            provider="sec_edgar",
            source_url=f"{_EDGAR_COMPANY_FACTS_BASE}/CIK{cik}.json",
            payload=payload,
            request_key=key,
        )
        return CompanyFacts(
            cik=cik,
            entity_name=payload.get("entityName"),
            facts=payload.get("facts", {}) or {},
            provenance=prov,
        )

    # ------------------------------------------------------------------
    # HTTP
    # ------------------------------------------------------------------
    def _get_json(self, url: str) -> dict[str, Any]:
        last_exc: Exception | None = None
        for attempt in range(self._max_retries + 1):
            _RATE_LIMITER.acquire()
            try:
                resp = self._http.get(url)
            except httpx.HTTPError as exc:
                last_exc = exc
                if attempt >= self._max_retries:
                    raise
                time.sleep(0.5 * (2 ** attempt))
                continue
            if resp.status_code == 429 or 500 <= resp.status_code < 600:
                last_exc = httpx.HTTPStatusError(
                    f"SEC EDGAR returned {resp.status_code}",
                    request=resp.request,
                    response=resp,
                )
                if attempt >= self._max_retries:
                    raise last_exc
                time.sleep(0.5 * (2 ** attempt))
                continue
            resp.raise_for_status()
            data = resp.json()
            if not isinstance(data, dict):
                raise RuntimeError(f"SEC EDGAR returned non-object JSON for {url}")
            return data
        if last_exc is not None:
            raise last_exc
        raise RuntimeError("SEC EDGAR request failed without an exception")


def _normalise_cik(cik: str | int) -> str:
    text = str(cik).strip().lstrip("CIK").lstrip("cik")
    if not text:
        raise ValueError("CIK cannot be empty")
    return f"{int(text):010d}"


def _zip_filings(recent: dict[str, Any]) -> list[Filing]:
    forms = recent.get("form") or []
    accessions = recent.get("accessionNumber") or []
    filing_dates = recent.get("filingDate") or []
    primary_docs = recent.get("primaryDocument") or []
    out: list[Filing] = []
    for i, form in enumerate(forms):
        try:
            filing_date = dt.date.fromisoformat(filing_dates[i])
        except (IndexError, ValueError):
            continue
        accession = accessions[i] if i < len(accessions) else ""
        primary = primary_docs[i] if i < len(primary_docs) else None
        out.append(
            Filing(
                accession_number=str(accession),
                form=str(form),
                filing_date=filing_date,
                primary_document=str(primary) if primary else None,
            )
        )
    return out


__all__ = (
    "CompanyFacts",
    "CompanySubmissions",
    "Filing",
    "SecEdgarClient",
    "SecEdgarConfigError",
)
