"""Phase-4 orchestrator.

Runs one (track, packet) collaborative cycle:

    market_climate (LLM tracks only)
        -> council fanout (parallel)
            -> skeptic + risk_manager (LLM tracks only, parallel)
                -> judge

Every step uses :class:`BaseAgent.run` so failures quarantine cleanly
instead of crashing the cycle. ``llm`` may be ``None`` for purely
deterministic baseline tracks; LLM tracks require an :class:`LLMClient`
(real ``OpenAIStructuredClient`` or the offline :class:`CannedCouncilLLM`).

Phase 4 makes IDs and timestamps deterministic for replay: the caller
passes ``window_run_id`` and a frozen ``now`` so every agent input is
reproducible.
"""

from __future__ import annotations

import asyncio
import datetime as dt
from dataclasses import dataclass
from typing import Any

from stockripper.agents.adversarial import (
    empty_risk_manager_report,
    empty_skeptic_report,
)
from stockripper.agents.base import BaseAgent
from stockripper.agents.council import empty_market_climate
from stockripper.agents.ids import track_run_id as derive_track_run_id
from stockripper.agents.llm import LLMClient
from stockripper.agents.registry import AgentRegistry
from stockripper.agents.schemas import (
    AgentRecommendation,
    AgentRunInput,
    AgentRunResult,
    AgentRunStatus,
    EvidencePacket,
    JudgeDecision,
    MarketClimateReport,
    RiskManagerReport,
    SkepticReport,
)


def _now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


@dataclass(frozen=True)
class TrackRunResult:
    """All :class:`AgentRunResult` envelopes produced by one ``run_track`` call."""

    track_id: str
    window_id: str
    window_run_id: str
    track_run_id: str
    packet: EvidencePacket
    market_climate_run: AgentRunResult | None
    council_runs: tuple[AgentRunResult, ...]
    skeptic_run: AgentRunResult | None
    risk_run: AgentRunResult | None
    judge_run: AgentRunResult
    started_at: dt.datetime

    @property
    def run_id(self) -> str:
        """Backwards-compat alias for ``track_run_id``."""
        return self.track_run_id

    @property
    def all_runs(self) -> tuple[AgentRunResult, ...]:
        parts: list[AgentRunResult] = []
        if self.market_climate_run is not None:
            parts.append(self.market_climate_run)
        parts.extend(self.council_runs)
        if self.skeptic_run is not None:
            parts.append(self.skeptic_run)
        if self.risk_run is not None:
            parts.append(self.risk_run)
        parts.append(self.judge_run)
        return tuple(parts)

    @property
    def council_recommendations(self) -> tuple[AgentRecommendation, ...]:
        return tuple(
            r.output
            for r in self.council_runs
            if r.status == AgentRunStatus.OK
            and isinstance(r.output, AgentRecommendation)
        )

    @property
    def market_climate(self) -> MarketClimateReport | None:
        run = self.market_climate_run
        if (
            run is not None
            and run.status == AgentRunStatus.OK
            and isinstance(run.output, MarketClimateReport)
        ):
            return run.output
        return None

    @property
    def skeptic_report(self) -> SkepticReport | None:
        if (
            self.skeptic_run is not None
            and self.skeptic_run.status == AgentRunStatus.OK
            and isinstance(self.skeptic_run.output, SkepticReport)
        ):
            return self.skeptic_run.output
        return None

    @property
    def risk_report(self) -> RiskManagerReport | None:
        if (
            self.risk_run is not None
            and self.risk_run.status == AgentRunStatus.OK
            and isinstance(self.risk_run.output, RiskManagerReport)
        ):
            return self.risk_run.output
        return None

    @property
    def judge_decision(self) -> JudgeDecision | None:
        if (
            self.judge_run.status == AgentRunStatus.OK
            and isinstance(self.judge_run.output, JudgeDecision)
        ):
            return self.judge_run.output
        return None

    @property
    def quarantined_runs(self) -> tuple[AgentRunResult, ...]:
        return tuple(
            r for r in self.all_runs if r.status == AgentRunStatus.QUARANTINED
        )


def _build_input(
    *,
    track_run_id: str,
    track_id: str,
    window_id: str,
    agent_id: str,
    packet: EvidencePacket,
    council_outputs: tuple[AgentRecommendation, ...] = (),
    market_climate: MarketClimateReport | None = None,
    skeptic_report: SkepticReport | None = None,
    risk_manager_report: RiskManagerReport | None = None,
    rng_seed: int | None = None,
    now: dt.datetime,
) -> AgentRunInput:
    return AgentRunInput(
        run_id=track_run_id,
        track_id=track_id,
        window_id=window_id,
        agent_id=agent_id,
        packet=packet,
        council_outputs=council_outputs,
        market_climate=market_climate,
        skeptic_report=skeptic_report,
        risk_manager_report=risk_manager_report,
        rng_seed=rng_seed,
        created_at=now,
    )


async def _run_one(
    agent: BaseAgent[Any],
    payload: AgentRunInput,
    *,
    llm: LLMClient | None,
    seed: int | None,
    now: dt.datetime,
) -> AgentRunResult:
    return await asyncio.to_thread(agent.run, payload, llm=llm, seed=seed, now=now)


async def run_track(
    *,
    registry: AgentRegistry,
    track_id: str,
    packet: EvidencePacket,
    llm: LLMClient | None = None,
    rng_seed: int | None = None,
    window_id: str | None = None,
    window_run_id: str | None = None,
    now: dt.datetime | None = None,
) -> TrackRunResult:
    """Run one (track, packet) collaborative cycle and return all envelopes.

    ``window_run_id`` and ``now`` should be supplied by the Strategy
    Tracks Manager (``window_runner.run_window``) so every agent input
    in the window shares a stable wall-clock and id. Defaults work for
    one-shot usage from the CLI but produce non-deterministic ids.
    """

    when = now if now is not None else _now()
    wid = window_id or packet.window_id
    if track_id not in registry.bindings:
        raise KeyError(f"unknown track_id: {track_id!r}")
    binding = registry.bindings[track_id]
    wrid = window_run_id or f"win_adhoc_{int(when.timestamp() * 1000):x}"
    track_run_id = derive_track_run_id(
        window_run_id=wrid,
        track_id=track_id,
        packet_id=packet.packet_id,
    )

    # Packet-aware fake clients (e.g. ``CannedCouncilLLM``) expose
    # ``bind_packet`` so they can synthesize symbol-correct outputs.
    # Binding inside ``run_track`` is safe because this coroutine
    # owns the client end-to-end. The window runner is responsible
    # for handing every concurrent ``run_track`` invocation its own
    # client instance to avoid cross-coroutine races.
    if llm is not None:
        bind_packet = getattr(llm, "bind_packet", None)
        if callable(bind_packet):
            bind_packet(packet)
        bind_clock = getattr(llm, "bind_clock", None)
        if callable(bind_clock):
            bind_clock(when)

    # ------------------------------------------------------------------
    # Step 1: market climate (LLM tracks only).
    # ------------------------------------------------------------------
    climate_run: AgentRunResult | None = None
    climate_for_council: MarketClimateReport | None = None
    if binding.market_climate_enabled:
        mc_input = _build_input(
            track_run_id=track_run_id,
            track_id=track_id,
            window_id=wid,
            agent_id=registry.market_climate.agent_id,
            packet=packet,
            rng_seed=rng_seed,
            now=when,
        )
        climate_run = await _run_one(
            registry.market_climate, mc_input, llm=llm, seed=rng_seed, now=when
        )
        if (
            climate_run.status == AgentRunStatus.OK
            and isinstance(climate_run.output, MarketClimateReport)
        ):
            climate_for_council = climate_run.output
        else:
            climate_for_council = empty_market_climate(as_of=when)

    # ------------------------------------------------------------------
    # Step 2: council fanout in parallel.
    # ------------------------------------------------------------------
    council_agents = registry.council_for(track_id)
    council_inputs = [
        _build_input(
            track_run_id=track_run_id,
            track_id=track_id,
            window_id=wid,
            agent_id=agent.agent_id,
            packet=packet,
            market_climate=climate_for_council,
            rng_seed=rng_seed,
            now=when,
        )
        for agent in council_agents
    ]
    council_runs = tuple(
        await asyncio.gather(
            *(
                _run_one(agent, payload, llm=llm, seed=rng_seed, now=when)
                for agent, payload in zip(
                    council_agents, council_inputs, strict=True
                )
            )
        )
    )
    recommendations = tuple(
        r.output
        for r in council_runs
        if r.status == AgentRunStatus.OK
        and isinstance(r.output, AgentRecommendation)
    )

    # ------------------------------------------------------------------
    # Step 3: adversarial pass (LLM tracks only).
    # ------------------------------------------------------------------
    skeptic_run: AgentRunResult | None = None
    risk_run: AgentRunResult | None = None
    skeptic_for_judge: SkepticReport | None = None
    risk_for_judge: RiskManagerReport | None = None
    if binding.is_llm_track and binding.adversarial_agent_ids:
        skeptic_input = _build_input(
            track_run_id=track_run_id,
            track_id=track_id,
            window_id=wid,
            agent_id=registry.skeptic.agent_id,
            packet=packet,
            council_outputs=recommendations,
            market_climate=climate_for_council,
            rng_seed=rng_seed,
            now=when,
        )
        risk_input = _build_input(
            track_run_id=track_run_id,
            track_id=track_id,
            window_id=wid,
            agent_id=registry.risk_manager.agent_id,
            packet=packet,
            council_outputs=recommendations,
            market_climate=climate_for_council,
            rng_seed=rng_seed,
            now=when,
        )
        skeptic_result, risk_result = await asyncio.gather(
            _run_one(registry.skeptic, skeptic_input, llm=llm, seed=rng_seed, now=when),
            _run_one(registry.risk_manager, risk_input, llm=llm, seed=rng_seed, now=when),
        )
        skeptic_run = skeptic_result
        risk_run = risk_result
        if (
            skeptic_result.status == AgentRunStatus.OK
            and isinstance(skeptic_result.output, SkepticReport)
        ):
            skeptic_for_judge = skeptic_result.output
        else:
            skeptic_for_judge = empty_skeptic_report(track_id=track_id, now=when)
        if (
            risk_result.status == AgentRunStatus.OK
            and isinstance(risk_result.output, RiskManagerReport)
        ):
            risk_for_judge = risk_result.output
        else:
            risk_for_judge = empty_risk_manager_report(track_id=track_id, now=when)

    # ------------------------------------------------------------------
    # Step 4: judge.
    # ------------------------------------------------------------------
    judge_agent = registry.judge_for(track_id)
    judge_input = _build_input(
        track_run_id=track_run_id,
        track_id=track_id,
        window_id=wid,
        agent_id=judge_agent.agent_id,
        packet=packet,
        council_outputs=recommendations,
        market_climate=climate_for_council,
        skeptic_report=skeptic_for_judge,
        risk_manager_report=risk_for_judge,
        rng_seed=rng_seed,
        now=when,
    )
    judge_llm = llm if judge_agent.requires_llm else None
    judge_run = await _run_one(
        judge_agent, judge_input, llm=judge_llm, seed=rng_seed, now=when
    )

    return TrackRunResult(
        track_id=track_id,
        window_id=wid,
        window_run_id=wrid,
        track_run_id=track_run_id,
        packet=packet,
        market_climate_run=climate_run,
        council_runs=council_runs,
        skeptic_run=skeptic_run,
        risk_run=risk_run,
        judge_run=judge_run,
        started_at=when,
    )


__all__ = ("TrackRunResult", "run_track")
