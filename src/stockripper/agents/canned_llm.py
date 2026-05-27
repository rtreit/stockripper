"""Deterministic structured-output LLM that synthesizes valid no-op responses.

Used by the Phase-4 orchestrator's ``--fake`` mode so the full council ->
adversarial -> judge pipeline can run end-to-end with zero network calls.

Design contract:

* Every synthesized response is a valid pydantic model for the requested
  schema. Council members emit HOLD (which requires no sizing and no
  evidence). Skeptic, risk manager, and market climate emit empty/neutral
  reports. The judge emits a CASH plan with no items.
* This is deliberately "boring but valid" — a CashPlan with all-HOLD
  recommendations is the truthful offline outcome. To see real trades
  without an LLM, run a deterministic baseline track (``benchmark``,
  ``random_baseline``, ``quant_signal``); to see real LLM-driven trades,
  pass ``--no-fake`` and supply OpenAI credentials.
"""

from __future__ import annotations

import datetime as dt
from collections.abc import Mapping
from decimal import Decimal
from typing import Any, TypeVar

from pydantic import BaseModel

from stockripper.agents.llm import StructuredResponse, _composite_digest
from stockripper.agents.schemas import (
    ActionPlan,
    AgentRecommendation,
    EvidencePacket,
    JudgeDecision,
    MarketClimateReport,
    MarketRegime,
    PortfolioPosture,
    PromptInjectionReport,
    RecommendationAction,
    RecommendationInstrument,
    RiskManagerReport,
    SkepticReport,
)

T = TypeVar("T", bound=BaseModel)


def _now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


class CannedCouncilLLM:
    """No-network ``LLMClient`` that synthesizes valid neutral outputs.

    The packet is provided at construction so the client is **stateless
    and safe to share** across concurrent agent calls within one
    (track, packet) execution. Phase-4 multi-track concurrency requires
    one ``CannedCouncilLLM`` instance per (track, packet) coroutine,
    which the window runner enforces.

    A frozen ``clock`` is optional and used only for deterministic
    timestamping inside synthesized outputs; tests should pass an
    explicit value so the synthesized created_at fields are reproducible.
    """

    def __init__(
        self,
        packet: EvidencePacket | None = None,
        *,
        model_id: str = "canned-council-v1",
        fixed_latency_ms: int = 1,
        clock: dt.datetime | None = None,
    ) -> None:
        self._model_id = model_id
        self._fixed_latency_ms = fixed_latency_ms
        self._packet: EvidencePacket | None = packet
        self._clock: dt.datetime | None = clock
        self.calls: list[Mapping[str, Any]] = []

    def bind_packet(self, packet: EvidencePacket) -> None:
        """Provide per-packet context for the next batch of synthesized calls.

        Retained for backwards compatibility; prefer passing ``packet`` in
        the constructor. Calling this on a shared instance from multiple
        coroutines is a race — construct a fresh client per coroutine.
        """

        self._packet = packet

    def bind_clock(self, clock: dt.datetime) -> None:
        """Freeze the wall-clock used for synthesized ``created_at`` fields."""

        self._clock = clock

    def _moment(self) -> dt.datetime:
        return self._clock if self._clock is not None else _now()

    def run_structured(
        self,
        *,
        prompt: str,
        schema: type[T],
        agent_id: str,
        model_id: str | None = None,
        seed: int | None = None,
        temperature: float = 0.0,
        top_p: float = 1.0,
        prompt_content_hash: str,
        schema_content_hash: str,
        input_content_hash: str,
    ) -> StructuredResponse:
        chosen_model = model_id or self._model_id
        digest = _composite_digest(
            model_id=chosen_model,
            temperature=temperature,
            top_p=top_p,
            seed=seed,
            prompt_content_hash=prompt_content_hash,
            schema_content_hash=schema_content_hash,
            input_content_hash=input_content_hash,
        )
        synthesized = self._synthesize(schema=schema, agent_id=agent_id)
        self.calls.append(
            {
                "agent_id": agent_id,
                "schema": schema.__name__,
                "digest": digest,
                "prompt_length": len(prompt),
                "model_id": chosen_model,
            }
        )
        return StructuredResponse(
            parsed=synthesized,
            raw_text=synthesized.model_dump_json(),
            model_id=chosen_model,
            latency_ms=self._fixed_latency_ms,
            finish_reason="stop",
            request_fingerprint_digest=digest,
        )

    # ------------------------------------------------------------------
    def _synthesize(self, *, schema: type[T], agent_id: str) -> T:
        if schema is AgentRecommendation:
            return self._make_recommendation(agent_id)  # type: ignore[return-value]
        if schema is MarketClimateReport:
            return self._make_market_climate(agent_id)  # type: ignore[return-value]
        if schema is SkepticReport:
            return self._make_skeptic_report(agent_id)  # type: ignore[return-value]
        if schema is RiskManagerReport:
            return self._make_risk_report(agent_id)  # type: ignore[return-value]
        if schema is JudgeDecision:
            return self._make_judge_decision(agent_id)  # type: ignore[return-value]
        if schema is PromptInjectionReport:
            return self._make_pi_report(agent_id)  # type: ignore[return-value]
        raise NotImplementedError(
            f"CannedCouncilLLM has no synthesizer for schema {schema.__name__!r}"
        )

    # ------------------------------------------------------------------
    def _make_recommendation(self, agent_id: str) -> AgentRecommendation:
        packet = self._require_packet(agent_id)
        when = self._moment()
        rec_seed = self._stable_id_seed(agent_id, "rec", packet.symbol)
        return AgentRecommendation(
            recommendation_id=f"rec_{rec_seed}",
            agent_id=agent_id,
            agent_version="1.0.0",
            track_id=packet.track_id,
            symbol=packet.symbol,
            instrument=_safe_recommendation_instrument(packet.instrument),
            action=RecommendationAction.HOLD,
            conviction=Decimal("0.10"),
            time_horizon_days=30,
            thesis=(
                f"[canned] {agent_id}: insufficient evidence in offline mode; "
                f"holding {packet.symbol}."
            ),
            evidence=(),
            risk_flags=(),
            prompt_injection_findings=(),
            multi_leg=None,
            pair_legs=None,
            created_at=when,
        )

    def _make_market_climate(self, agent_id: str) -> MarketClimateReport:
        when = self._moment()
        seed = self._stable_id_seed(agent_id, "climate", when.date().isoformat())
        return MarketClimateReport(
            report_id=f"climate_{seed}",
            agent_id=agent_id,
            agent_version="1.0.0",
            as_of=when.date(),
            regime=MarketRegime.UNCERTAIN,
            breadth_score=Decimal("0"),
            volatility_score=Decimal("0"),
            rates_backdrop="unknown",
            sector_rotation=(),
            narrative="[canned] No external macro evidence in offline mode.",
            evidence=(),
            created_at=when,
        )

    def _make_skeptic_report(self, agent_id: str) -> SkepticReport:
        packet = self._require_packet(agent_id)
        when = self._moment()
        seed = self._stable_id_seed(agent_id, "skeptic", packet.symbol)
        return SkepticReport(
            report_id=f"skeptic_{seed}",
            agent_id=agent_id,
            agent_version="1.0.0",
            track_id=packet.track_id,
            critiques=(),
            created_at=when,
        )

    def _make_risk_report(self, agent_id: str) -> RiskManagerReport:
        packet = self._require_packet(agent_id)
        when = self._moment()
        seed = self._stable_id_seed(agent_id, "risk", packet.symbol)
        return RiskManagerReport(
            report_id=f"risk_{seed}",
            agent_id=agent_id,
            agent_version="1.0.0",
            track_id=packet.track_id,
            assessments=(),
            portfolio_level_flags=(),
            created_at=when,
        )

    def _make_judge_decision(self, agent_id: str) -> JudgeDecision:
        packet = self._require_packet(agent_id)
        when = self._moment()
        seed = self._stable_id_seed(agent_id, "plan", packet.symbol)
        plan = ActionPlan(
            decision_id=f"plan_{seed}",
            track_id=packet.track_id,
            judge_agent_id=agent_id,
            judge_agent_version="1.0.0",
            portfolio_posture=PortfolioPosture.CASH,
            items=(),
            rationale=(
                "[canned] Offline judge: council emitted only HOLD signals; "
                "holding cash for this window."
            ),
            objective_label="offline_canned_judge",
            created_at=when,
        )
        return JudgeDecision(plan=plan, provenance=None)

    def _make_pi_report(self, agent_id: str) -> PromptInjectionReport:
        when = self._moment()
        seed = self._stable_id_seed(agent_id, "pi")
        return PromptInjectionReport(
            report_id=f"pi_{seed}",
            agent_id=agent_id,
            agent_version="1.0.0",
            findings=(),
            scanned_evidence_ids=(),
            created_at=when,
        )

    # ------------------------------------------------------------------
    def _stable_id_seed(self, *parts: str) -> str:
        """Deterministic 16-hex seed derived from caller-provided parts.

        We mix in the model id + packet id so two coroutines that share a
        Canned client by accident at least produce distinguishable ids,
        but the same (model, packet, parts) always produces the same id.
        """

        import hashlib

        body_parts: list[str] = [self._model_id]
        if self._packet is not None:
            body_parts.append(self._packet.packet_id)
        body_parts.extend(parts)
        body = "\x00".join(body_parts)
        return hashlib.sha256(body.encode("utf-8")).hexdigest()[:16]

    def _require_packet(self, agent_id: str) -> EvidencePacket:
        if self._packet is None:
            raise RuntimeError(
                f"CannedCouncilLLM: packet not provided (construct with "
                f"packet=… or call bind_packet) before {agent_id!r}."
            )
        return self._packet


_PACKET_TO_RECOMMENDATION_INSTRUMENT: dict[
    RecommendationInstrument, RecommendationInstrument
] = {
    # Most packet instruments map 1:1.
    RecommendationInstrument.EQUITY: RecommendationInstrument.EQUITY,
    RecommendationInstrument.ETF: RecommendationInstrument.ETF,
    RecommendationInstrument.LEVERAGED_ETF: RecommendationInstrument.LEVERAGED_ETF,
    # Option packets degenerate to a safe "underlying-equity HOLD" so HOLD
    # recommendations don't get caught by multi-leg/pair shape validators.
    RecommendationInstrument.OPTION_SINGLE: RecommendationInstrument.EQUITY,
    RecommendationInstrument.MULTI_LEG_OPTION: RecommendationInstrument.EQUITY,
    RecommendationInstrument.PAIR: RecommendationInstrument.EQUITY,
}


def _safe_recommendation_instrument(
    packet_instrument: RecommendationInstrument,
) -> RecommendationInstrument:
    """Pick an instrument that lets HOLD pass the multi-leg/pair validators."""

    return _PACKET_TO_RECOMMENDATION_INSTRUMENT[packet_instrument]


__all__ = ("CannedCouncilLLM",)
