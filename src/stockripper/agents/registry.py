"""Agent registry — single source of truth for which agents run per track.

Spec §7.2 defines the per-track weighting table. We encode "included
agents" and "excluded agents" sets for each LLM-driven track, and bind
the dedicated baselines to their three baseline tracks.

The registry is the surface the LangGraph orchestrator (Phase 4) and the
``stockripper agents`` CLI consume.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass, field
from typing import Final

from pydantic import BaseModel

from stockripper.agents.adversarial import (
    PromptInjectionDetector,
    RiskManagerAgent,
    SkepticAgent,
)
from stockripper.agents.base import BaseAgent
from stockripper.agents.baselines import (
    BenchmarkAgent,
    QuantSignalAgent,
    RandomBaselineAgent,
)
from stockripper.agents.council import (
    COUNCIL,
    CouncilAgent,
    MarketClimateAgent,
)
from stockripper.agents.judges import JUDGES, JudgeAgent

# Per spec §7.2:  excluded_agents lists which council members do NOT run
# on each track. By default ALL council members run unless excluded.
_LLM_TRACK_EXCLUSIONS: Final[dict[str, frozenset[str]]] = {
    "conservative": frozenset(
        {
            "squeeze_hunter",
            "leveraged_etf_tactician",
            "options_speculator",
            "spread_strategist",
            "short_seller",
            "crisis_alpha",
            "news_velocity",
            "catalyst_sniper",
        }
    ),
    "balanced": frozenset({"squeeze_hunter"}),
    "aggressive": frozenset({}),
    "concentrated": frozenset(
        {
            "news_velocity",
            "leveraged_etf_tactician",
            "pair_trade_arb",
        }
    ),
    "yolo": frozenset({}),
}

# Adversarial agents that always run on LLM tracks.
_ALWAYS_ON_ADVERSARIAL: Final[tuple[str, ...]] = (
    "skeptic",
    "risk_manager",
    "prompt_injection_detector",
)


@dataclass(frozen=True)
class TrackBinding:
    """Resolved agent membership for one strategy track."""

    track_id: str
    judge_id: str
    council_agent_ids: tuple[str, ...]
    adversarial_agent_ids: tuple[str, ...]
    market_climate_enabled: bool
    is_llm_track: bool


@dataclass(frozen=True)
class AgentRegistry:
    """In-memory snapshot of all agents + their per-track bindings."""

    council: dict[str, CouncilAgent] = field(default_factory=dict)
    market_climate: MarketClimateAgent = field(default_factory=MarketClimateAgent)
    skeptic: SkepticAgent = field(default_factory=SkepticAgent)
    risk_manager: RiskManagerAgent = field(default_factory=RiskManagerAgent)
    prompt_injection: PromptInjectionDetector = field(default_factory=PromptInjectionDetector)
    judges: dict[str, JudgeAgent] = field(default_factory=dict)
    baselines: dict[str, BaseAgent[BaseModel]] = field(default_factory=dict)
    bindings: dict[str, TrackBinding] = field(default_factory=dict)

    def llm_track_ids(self) -> tuple[str, ...]:
        return tuple(b.track_id for b in self.bindings.values() if b.is_llm_track)

    def baseline_track_ids(self) -> tuple[str, ...]:
        return tuple(b.track_id for b in self.bindings.values() if not b.is_llm_track)

    def council_for(self, track_id: str) -> tuple[CouncilAgent, ...]:
        b = self.bindings[track_id]
        return tuple(self.council[aid] for aid in b.council_agent_ids)

    def judge_for(self, track_id: str) -> BaseAgent[BaseModel]:
        b = self.bindings[track_id]
        if b.is_llm_track:
            return self.judges[b.judge_id]
        return self.baselines[track_id]


def build_registry() -> AgentRegistry:
    council = {spec.agent_id: CouncilAgent(spec) for spec in COUNCIL}
    judges = {j.spec.judge_id: j for j in (JudgeAgent(spec) for spec in JUDGES)}
    baselines: dict[str, BaseAgent[BaseModel]] = {
        "quant_signal": QuantSignalAgent(),
        "random_baseline": RandomBaselineAgent(),
        "benchmark": BenchmarkAgent(),
    }

    bindings: dict[str, TrackBinding] = {}

    for judge_spec in JUDGES:
        excl = _LLM_TRACK_EXCLUSIONS.get(judge_spec.track_id, frozenset())
        council_ids = tuple(
            spec.agent_id for spec in COUNCIL if spec.agent_id not in excl
        )
        bindings[judge_spec.track_id] = TrackBinding(
            track_id=judge_spec.track_id,
            judge_id=judge_spec.judge_id,
            council_agent_ids=council_ids,
            adversarial_agent_ids=_ALWAYS_ON_ADVERSARIAL,
            market_climate_enabled=True,
            is_llm_track=True,
        )

    for baseline_track_id in ("quant_signal", "random_baseline", "benchmark"):
        bindings[baseline_track_id] = TrackBinding(
            track_id=baseline_track_id,
            judge_id=baselines[baseline_track_id].agent_id,
            council_agent_ids=tuple(spec.agent_id for spec in COUNCIL),
            adversarial_agent_ids=(),
            market_climate_enabled=False,
            is_llm_track=False,
        )

    return AgentRegistry(
        council=council,
        judges=judges,
        baselines=baselines,
        bindings=bindings,
    )


def list_all_agent_ids(registry: AgentRegistry) -> Iterable[str]:
    yield from registry.council
    yield registry.market_climate.agent_id
    yield registry.skeptic.agent_id
    yield registry.risk_manager.agent_id
    yield registry.prompt_injection.agent_id
    yield from registry.judges
    yield from registry.baselines


__all__ = (
    "AgentRegistry",
    "TrackBinding",
    "build_registry",
    "list_all_agent_ids",
)
