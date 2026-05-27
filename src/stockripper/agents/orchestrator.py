"""Phase-4 minimal-slice orchestrator.

Runs one (track, packet) collaborative cycle:

    market_climate (LLM tracks only)
        -> council fanout (parallel)
            -> skeptic + risk_manager (LLM tracks only, parallel)
                -> judge

Every step uses :class:`BaseAgent.run` so failures quarantine cleanly
instead of crashing the cycle. ``llm`` may be ``None`` for purely
deterministic baseline tracks; LLM tracks require an :class:`LLMClient`
(real ``OpenAIStructuredClient`` or the offline :class:`CannedCouncilLLM`).

This is intentionally NOT LangGraph — it is the smallest useful slice
that exercises every Phase-3 surface end-to-end so we can observe agents
collaborating. Real graph wiring, checkpoints, persistence of
``agent_runs`` / ``judge_decisions``, and parallel sub-graphs per track
all land in the full Phase 4 PR.
"""

from __future__ import annotations

import asyncio
import datetime as dt
import uuid
from dataclasses import dataclass
from typing import Any

from stockripper.agents.adversarial import (
    empty_risk_manager_report,
    empty_skeptic_report,
)
from stockripper.agents.base import BaseAgent
from stockripper.agents.canned_llm import CannedCouncilLLM
from stockripper.agents.council import empty_market_climate
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
    run_id: str
    packet: EvidencePacket
    market_climate_run: AgentRunResult | None
    council_runs: tuple[AgentRunResult, ...]
    skeptic_run: AgentRunResult | None
    risk_run: AgentRunResult | None
    judge_run: AgentRunResult

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
        run_id=f"run_{uuid.uuid4().hex[:16]}",
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
) -> AgentRunResult:
    return await asyncio.to_thread(agent.run, payload, llm=llm, seed=seed)


async def run_track(
    *,
    registry: AgentRegistry,
    track_id: str,
    packet: EvidencePacket,
    llm: LLMClient | None = None,
    rng_seed: int | None = None,
    window_id: str | None = None,
    now: dt.datetime | None = None,
) -> TrackRunResult:
    """Run one (track, packet) collaborative cycle and return all envelopes."""

    when = now if now is not None else _now()
    wid = window_id or packet.window_id
    if track_id not in registry.bindings:
        raise KeyError(f"unknown track_id: {track_id!r}")
    binding = registry.bindings[track_id]
    run_id = f"trackrun_{uuid.uuid4().hex[:16]}"

    if isinstance(llm, CannedCouncilLLM):
        llm.bind_packet(packet)

    # ------------------------------------------------------------------
    # Step 1: market climate (LLM tracks only).
    # ------------------------------------------------------------------
    climate_run: AgentRunResult | None = None
    climate_for_council: MarketClimateReport | None = None
    if binding.market_climate_enabled:
        mc_input = _build_input(
            track_id=track_id,
            window_id=wid,
            agent_id=registry.market_climate.agent_id,
            packet=packet,
            rng_seed=rng_seed,
            now=when,
        )
        climate_run = await _run_one(
            registry.market_climate, mc_input, llm=llm, seed=rng_seed
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
                _run_one(agent, payload, llm=llm, seed=rng_seed)
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
            _run_one(registry.skeptic, skeptic_input, llm=llm, seed=rng_seed),
            _run_one(registry.risk_manager, risk_input, llm=llm, seed=rng_seed),
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
    judge_run = await _run_one(judge_agent, judge_input, llm=judge_llm, seed=rng_seed)

    return TrackRunResult(
        track_id=track_id,
        window_id=wid,
        run_id=run_id,
        packet=packet,
        market_climate_run=climate_run,
        council_runs=council_runs,
        skeptic_run=skeptic_run,
        risk_run=risk_run,
        judge_run=judge_run,
    )


__all__ = ("TrackRunResult", "run_track")
