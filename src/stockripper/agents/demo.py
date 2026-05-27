"""Synthesize a minimal :class:`EvidencePacket` from a symbol + light metadata.

Used by ``stockripper agents run-track`` so the CLI can demo the
collaborative pipeline without a full Phase-2 universe build. Production
runs would call :func:`stockripper.agents.evidence.build_evidence_packet`
directly with real ``Candidate`` instances.
"""

from __future__ import annotations

import datetime as dt
import uuid
from decimal import Decimal

from stockripper.agents.schemas import (
    EvidencePacket,
    RecommendationInstrument,
)
from stockripper.data.reasons import CandidateReason, CandidateReasonCode


def _now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def build_demo_packet(
    *,
    symbol: str,
    track_id: str,
    window_id: str | None = None,
    last_price: Decimal | None = None,
    adv_usd_20d: Decimal | None = None,
    market_cap_usd: Decimal | None = None,
    recent_8k_within_days: int | None = None,
    recent_news_count_30d: int | None = None,
    instrument: RecommendationInstrument = RecommendationInstrument.EQUITY,
    bucket: str = "core",
    now: dt.datetime | None = None,
) -> EvidencePacket:
    """Build a self-contained packet for offline orchestrator demos."""

    when = now if now is not None else _now()
    wid = window_id or when.strftime("demo-%Y%m%dT%H%M%SZ")
    reasons: tuple[CandidateReason, ...] = (
        CandidateReason(
            code=CandidateReasonCode.PASSES_PRICE_FLOOR,
            params={"instrument": "equity_long"},
        ),
        CandidateReason(
            code=CandidateReasonCode.PASSES_ADV_FLOOR,
            params={"adv_usd_20d": str(adv_usd_20d) if adv_usd_20d is not None else "unknown"},
        ),
    )
    summary_bits = [f"symbol={symbol}", f"bucket={bucket}"]
    if last_price is not None:
        summary_bits.append(f"price=${last_price}")
    if adv_usd_20d is not None:
        summary_bits.append(f"adv20=${int(adv_usd_20d):,}")
    if market_cap_usd is not None:
        summary_bits.append(f"cap=${int(market_cap_usd):,}")
    if recent_8k_within_days is not None:
        summary_bits.append(f"last_8k={recent_8k_within_days}d")
    if recent_news_count_30d is not None:
        summary_bits.append(f"news30={recent_news_count_30d}")
    summary = " ".join(summary_bits) or f"symbol={symbol}"
    return EvidencePacket(
        packet_id=f"pkt_demo_{uuid.uuid4().hex[:12]}",
        track_id=track_id,
        window_id=wid,
        symbol=symbol.upper(),
        instrument=instrument,
        candidate_reasons=reasons,
        snapshot_summary=summary,
        evidence_refs=(),
        provenances=(),
        prompt_injection_report=None,
        as_of=when,
    )


__all__ = ("build_demo_packet",)
