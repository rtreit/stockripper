"""Build :class:`EvidencePacket` instances from Phase-2 adapter outputs.

Sits at the seam between Phase 2 (data adapters / universe builder) and
Phase 3 (agents). Phase 4 orchestration will call this once per (track,
window, candidate) before fanning out to the council.

Design rule: this module pulls evidence references *by URI and hash*. It
must NOT embed raw retrieved content in the packet — keeping packets
small is the entire reason LangGraph checkpoints stay cheap in Phase 4.
"""

from __future__ import annotations

import datetime as dt
import uuid
from collections.abc import Iterable, Sequence

from stockripper.agents.prompt_injection import scan_evidence
from stockripper.agents.sanitizer import sanitize_content
from stockripper.agents.schemas import (
    CandidateEvidenceRef,
    EvidencePacket,
    EvidenceSourceType,
    PromptInjectionReport,
    RecommendationInstrument,
)
from stockripper.data.provenance import Provenance
from stockripper.data.universe import Candidate
from stockripper.data.universe_policy import InstrumentType as UniverseInstrumentType


def _now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def _instrument_from_universe(uni: UniverseInstrumentType) -> RecommendationInstrument:
    mapping: dict[UniverseInstrumentType, RecommendationInstrument] = {
        UniverseInstrumentType.EQUITY_LONG: RecommendationInstrument.EQUITY,
        UniverseInstrumentType.EQUITY_SHORT: RecommendationInstrument.EQUITY,
        UniverseInstrumentType.ETF: RecommendationInstrument.ETF,
        UniverseInstrumentType.LEVERAGED_ETF: RecommendationInstrument.LEVERAGED_ETF,
        UniverseInstrumentType.OPTION_SINGLE: RecommendationInstrument.OPTION_SINGLE,
        UniverseInstrumentType.OPTION_SPREAD: RecommendationInstrument.MULTI_LEG_OPTION,
    }
    return mapping[uni]


def _summarize_snapshot(candidate: Candidate) -> str:
    snap = candidate.snapshot
    cap = (
        f"${int(snap.market_cap_usd):,}" if snap.market_cap_usd is not None else "n/a"
    )
    news = (
        str(snap.recent_news_count_30d) if snap.recent_news_count_30d is not None else "n/a"
    )
    catalyst = (
        f"{snap.recent_8k_within_days}d" if snap.recent_8k_within_days is not None else "n/a"
    )
    return (
        f"price=${snap.last_price} adv20=${int(snap.adv_usd_20d):,} "
        f"cap={cap} news30={news} last_8k={catalyst} bucket={candidate.bucket}"
    )


def build_evidence_packet(
    *,
    track_id: str,
    window_id: str,
    candidate: Candidate,
    evidence_excerpts: Sequence[tuple[EvidenceSourceType, str, str]] = (),
    provenances: Iterable[Provenance] = (),
    instrument: RecommendationInstrument | None = None,
    now: dt.datetime | None = None,
) -> EvidencePacket:
    """Assemble a serializable :class:`EvidencePacket` for one candidate.

    ``evidence_excerpts`` is a sequence of ``(source_type, source_url,
    raw_text)`` tuples. Each excerpt is sanitized, hashed, and turned
    into a :class:`CandidateEvidenceRef`; the sanitized text is then
    scanned for prompt-injection patterns and the report is attached to
    the packet so the council/judge skip injected payloads.
    """

    timestamp = now if now is not None else _now()
    refs: list[CandidateEvidenceRef] = []
    scan_inputs: list[tuple[str, str]] = []

    for source_type, source_url, raw_text in evidence_excerpts:
        san = sanitize_content(raw_text)
        ev_id = f"evref_{uuid.uuid4().hex[:16]}"
        prov = Provenance.for_payload(
            provider=source_type.value,
            source_url=source_url,
            payload=raw_text,
        )
        refs.append(
            CandidateEvidenceRef(
                source_type=source_type,
                source_url=source_url,
                raw_content_uri=None,
                content_hash=prov.content_hash,
                retrieved_at=timestamp,
                summary=san.sanitized[:280] if san.sanitized else "(empty after sanitization)",
            )
        )
        scan_inputs.append((ev_id, san.sanitized))

    pi_report: PromptInjectionReport | None = (
        scan_evidence(scan_inputs, track_id=track_id, now=timestamp)
        if scan_inputs
        else None
    )

    resolved_instrument = (
        instrument
        if instrument is not None
        else _instrument_from_universe(_infer_universe_instrument(candidate))
    )

    return EvidencePacket(
        packet_id=f"pkt_{uuid.uuid4().hex[:16]}",
        track_id=track_id,
        window_id=window_id,
        symbol=candidate.symbol,
        instrument=resolved_instrument,
        candidate_reasons=candidate.reasons,
        snapshot_summary=_summarize_snapshot(candidate),
        evidence_refs=tuple(refs),
        provenances=tuple(provenances),
        prompt_injection_report=pi_report,
        as_of=timestamp,
    )


def _infer_universe_instrument(candidate: Candidate) -> UniverseInstrumentType:
    """Recover the universe-instrument type from a candidate's reasons."""

    for reason in candidate.reasons:
        instrument_label = reason.params.get("instrument") if reason.params else None
        if instrument_label:
            try:
                return UniverseInstrumentType(instrument_label)
            except ValueError:
                continue
    return UniverseInstrumentType.EQUITY_LONG


__all__ = ("build_evidence_packet",)
