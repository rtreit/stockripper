"""Tests for the Phase-4 minimal-slice orchestrator."""

from __future__ import annotations

import asyncio
import datetime as dt
from collections.abc import Coroutine
from decimal import Decimal
from typing import Any

import pytest

from stockripper.agents.canned_llm import CannedCouncilLLM
from stockripper.agents.demo import build_demo_packet
from stockripper.agents.llm import FakeLLMClient
from stockripper.agents.orchestrator import TrackRunResult, run_track
from stockripper.agents.registry import AgentRegistry, build_registry
from stockripper.agents.schemas import (
    AgentRecommendation,
    AgentRunStatus,
    EvidencePacket,
    JudgeDecision,
    RecommendationAction,
    RecommendationInstrument,
)


@pytest.fixture(scope="module")
def registry() -> AgentRegistry:
    return build_registry()


@pytest.fixture
def packet() -> EvidencePacket:
    return build_demo_packet(
        symbol="AAPL",
        track_id="balanced",
        last_price=Decimal("220"),
        adv_usd_20d=Decimal("50000000000"),
        market_cap_usd=Decimal("3500000000000"),
        recent_8k_within_days=12,
        recent_news_count_30d=8,
    )


def _run[T](coro: Coroutine[Any, Any, T]) -> T:
    return asyncio.run(coro)


def test_canned_llm_track_returns_judge_decision(
    registry: AgentRegistry, packet: EvidencePacket
) -> None:
    llm = CannedCouncilLLM()
    result = _run(
        run_track(
            registry=registry,
            track_id="balanced",
            packet=packet,
            llm=llm,
            rng_seed=7,
        )
    )
    assert isinstance(result, TrackRunResult)
    assert result.track_id == "balanced"
    assert result.market_climate_run is not None
    assert result.market_climate is not None
    assert result.skeptic_report is not None
    assert result.risk_report is not None

    binding = registry.bindings["balanced"]
    assert len(result.council_runs) == len(binding.council_agent_ids)

    ok_runs = [r for r in result.council_runs if r.status == AgentRunStatus.OK]
    assert len(ok_runs) == len(result.council_runs)
    for run in ok_runs:
        assert isinstance(run.output, AgentRecommendation)
        assert run.output.action == RecommendationAction.HOLD

    decision = result.judge_decision
    assert isinstance(decision, JudgeDecision)
    assert decision.plan.judge_agent_id == "judge_balanced"


def test_canned_llm_track_calls_every_agent(
    registry: AgentRegistry, packet: EvidencePacket
) -> None:
    llm = CannedCouncilLLM()
    _run(
        run_track(
            registry=registry,
            track_id="aggressive",
            packet=packet.model_copy(update={"track_id": "aggressive"}),
            llm=llm,
        )
    )
    binding = registry.bindings["aggressive"]
    # adversarial_agent_ids lists 3 (skeptic, risk_manager, prompt_injection_detector)
    # but the PI detector runs at evidence-packet build time, not during the
    # orchestrator run. So 2 adversarial LLM calls fire here.
    expected = (
        1  # market_climate
        + len(binding.council_agent_ids)
        + 2  # skeptic + risk_manager
        + 1  # judge
    )
    assert len(llm.calls) == expected


def test_baseline_track_benchmark_emits_buy_spy(
    registry: AgentRegistry,
) -> None:
    packet = build_demo_packet(symbol="SPY", track_id="benchmark")
    llm = CannedCouncilLLM()
    result = _run(
        run_track(
            registry=registry,
            track_id="benchmark",
            packet=packet,
            llm=llm,
        )
    )
    assert result.market_climate_run is None
    assert result.skeptic_run is None
    assert result.risk_run is None
    decision = result.judge_decision
    assert decision is not None
    assert len(decision.plan.items) == 1
    item = decision.plan.items[0]
    assert item.symbol == "SPY"
    assert item.instrument == RecommendationInstrument.ETF


def test_unknown_track_raises_key_error(
    registry: AgentRegistry, packet: EvidencePacket
) -> None:
    with pytest.raises(KeyError):
        _run(
            run_track(
                registry=registry,
                track_id="does-not-exist",
                packet=packet,
                llm=CannedCouncilLLM(),
            )
        )


def test_council_quarantines_when_llm_returns_garbage(
    registry: AgentRegistry, packet: EvidencePacket
) -> None:
    # FakeLLMClient with no canned responses -> KeyError -> quarantine.
    broken_llm = FakeLLMClient(canned={})
    result = _run(
        run_track(
            registry=registry,
            track_id="balanced",
            packet=packet,
            llm=broken_llm,
        )
    )
    assert all(r.status == AgentRunStatus.QUARANTINED for r in result.council_runs)
    # Judge ran with empty council recommendations; status still defined.
    assert result.judge_run is not None
    # Skeptic and risk still ran (and also quarantined under FakeLLMClient).
    assert result.skeptic_run is not None
    assert result.risk_run is not None
    assert result.skeptic_run.status == AgentRunStatus.QUARANTINED
    assert result.risk_run.status == AgentRunStatus.QUARANTINED
    # judge_decision can be None when judge quarantined under garbage LLM.
    if result.judge_decision is not None:
        assert isinstance(result.judge_decision, JudgeDecision)


def test_run_track_propagates_rng_seed(
    registry: AgentRegistry, packet: EvidencePacket
) -> None:
    llm = CannedCouncilLLM()
    result = _run(
        run_track(
            registry=registry,
            track_id="balanced",
            packet=packet,
            llm=llm,
            rng_seed=123,
        )
    )
    for r in result.all_runs:
        if r.status == AgentRunStatus.OK:
            assert r.fingerprint.seed == 123


def test_run_track_window_id_override(
    registry: AgentRegistry, packet: EvidencePacket
) -> None:
    llm = CannedCouncilLLM()
    result = _run(
        run_track(
            registry=registry,
            track_id="balanced",
            packet=packet,
            llm=llm,
            window_id="explicit-window-2026Q2",
        )
    )
    assert result.window_id == "explicit-window-2026Q2"


def test_run_track_uses_canned_clock_when_supplied(
    registry: AgentRegistry, packet: EvidencePacket
) -> None:
    now = dt.datetime(2026, 5, 27, 19, 0, 0, tzinfo=dt.UTC)
    llm = CannedCouncilLLM()
    result = _run(
        run_track(
            registry=registry,
            track_id="balanced",
            packet=packet,
            llm=llm,
            now=now,
        )
    )
    assert result.market_climate is not None
    assert result.market_climate.as_of == now.date()
