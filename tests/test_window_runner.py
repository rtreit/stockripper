"""Tests for the Phase-4 Strategy Tracks Manager (window_runner).

Covers:
* Multi-(track, symbol) fan-out and persistence.
* Kill-switch pre-flight aborts everything with status=aborted_kill.
* Per-track pause skips the track but lets siblings run.
* Failures inside one track do not crash the window.
* Replay determinism: re-running with the same inputs reproduces identical
  ``run_id`` / ``track_run_id`` / ``decision_id`` / ``action_id`` values.
"""

from __future__ import annotations

import datetime as dt

import pytest
from sqlalchemy.orm import Session, sessionmaker

from stockripper.agents.canned_llm import CannedCouncilLLM
from stockripper.agents.registry import build_registry
from stockripper.agents.schemas import EvidencePacket
from stockripper.agents.schemas import JudgeDecision as JudgeDecisionSchema
from stockripper.agents.window_runner import run_window
from stockripper.db import Base, Repository, build_engine
from stockripper.db.models import AgentRun, JudgeDecision, Run, TrackRun
from stockripper.tracks import seed_default_tracks

_FROZEN_NOW = dt.datetime(2026, 5, 28, 14, 30, 0, tzinfo=dt.UTC)


@pytest.fixture
def session_factory() -> sessionmaker[Session]:
    engine = build_engine("sqlite:///:memory:")
    Base.metadata.create_all(bind=engine)
    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    s = factory()
    seed_default_tracks(s)
    s.commit()
    s.close()
    return factory


def _llm_factory(packet: EvidencePacket) -> CannedCouncilLLM:
    return CannedCouncilLLM(packet=packet, clock=_FROZEN_NOW)


async def test_run_window_persists_per_track_audit(
    session_factory: sessionmaker[Session],
) -> None:
    registry = build_registry()
    result = await run_window(
        registry=registry,
        track_ids=("balanced", "yolo"),
        symbols=("AAPL", "MSFT"),
        window_label="opening",
        config_hash="cfg-test",
        rng_seed=42,
        now=_FROZEN_NOW,
        llm_factory=_llm_factory,
        session_factory=session_factory,
    )
    assert result.status == "ok"
    assert len(result.outcomes) == 4
    assert all(o.status == "ok" for o in result.outcomes)

    with session_factory() as s:
        repo = Repository(s)
        run = repo.get_run(result.run_id)
        assert run is not None
        assert run.status == "ok"
        assert run.completed_at is not None
        track_runs = repo.list_track_runs(run_id=result.run_id)
        assert len(track_runs) == 4
        # Each track-run should have produced council + adversarial + judge agent runs.
        agent_runs = s.query(AgentRun).filter(AgentRun.run_id == result.run_id).all()
        assert len(agent_runs) >= 4  # at least one judge per track-run
        decisions = s.query(JudgeDecision).filter(
            JudgeDecision.run_id == result.run_id
        ).all()
        assert len(decisions) == 4


async def test_run_window_kill_switch_aborts_everything(
    session_factory: sessionmaker[Session],
) -> None:
    registry = build_registry()
    with session_factory() as s:
        repo = Repository(s)
        repo.engage_kill_switch(reason="ops_drill", engaged_by="test", when=_FROZEN_NOW)
        s.commit()

    result = await run_window(
        registry=registry,
        track_ids=("balanced", "yolo"),
        symbols=("AAPL",),
        window_label="opening",
        config_hash="cfg-kill",
        now=_FROZEN_NOW,
        llm_factory=_llm_factory,
        session_factory=session_factory,
    )
    assert result.status == "aborted_kill"
    assert all(o.status == "aborted_kill" for o in result.outcomes)

    with session_factory() as s:
        run = s.query(Run).filter(Run.run_id == result.run_id).one()
        assert run.status == "aborted_kill"
        track_runs = s.query(TrackRun).filter(TrackRun.run_id == result.run_id).all()
        assert len(track_runs) == 2
        for tr in track_runs:
            assert tr.status == "aborted_kill"
            assert tr.interrupt_reason is not None
            assert "kill" in tr.interrupt_reason


async def test_run_window_pause_skips_only_paused_track(
    session_factory: sessionmaker[Session],
) -> None:
    registry = build_registry()
    with session_factory() as s:
        repo = Repository(s)
        repo.pause_track(track_id="yolo", reason="manual", when=_FROZEN_NOW)
        s.commit()

    result = await run_window(
        registry=registry,
        track_ids=("balanced", "yolo"),
        symbols=("AAPL",),
        window_label="opening",
        config_hash="cfg-pause",
        now=_FROZEN_NOW,
        llm_factory=_llm_factory,
        session_factory=session_factory,
    )
    assert result.status == "ok"
    skipped = [o for o in result.outcomes if o.status == "skipped_paused"]
    okays = [o for o in result.outcomes if o.status == "ok"]
    assert len(skipped) == 1 and skipped[0].track_id == "yolo"
    assert len(okays) == 1 and okays[0].track_id == "balanced"

    with session_factory() as s:
        track_runs = s.query(TrackRun).filter(TrackRun.run_id == result.run_id).all()
        assert len(track_runs) == 2
        statuses = {tr.track_id: tr.status for tr in track_runs}
        assert statuses["yolo"] == "skipped_paused"
        assert statuses["balanced"] == "ok"


async def test_run_window_isolates_one_track_failure(
    session_factory: sessionmaker[Session],
) -> None:
    registry = build_registry()
    healthy_packet_factory = _llm_factory

    def _selective_factory(packet: EvidencePacket) -> CannedCouncilLLM | None:
        # For "yolo" we hand back a stub LLM that raises on every call.
        # ``BaseAgent.run`` converts the RuntimeError into a quarantined
        # AgentRunResult, so the yolo track-run lands as ``partial``
        # instead of crashing the window.
        if packet.track_id == "yolo":
            class _Boom:
                def bind_packet(self, *_a: object, **_kw: object) -> None: ...
                def bind_clock(self, *_a: object, **_kw: object) -> None: ...
                def run_structured(
                    self, *_a: object, **_kw: object,
                ) -> object:
                    raise RuntimeError("synthetic LLM failure for yolo")
            return _Boom()  # type: ignore[return-value]
        return healthy_packet_factory(packet)

    result = await run_window(
        registry=registry,
        track_ids=("balanced", "yolo"),
        symbols=("AAPL",),
        window_label="opening",
        config_hash="cfg-iso",
        now=_FROZEN_NOW,
        llm_factory=_selective_factory,
        session_factory=session_factory,
    )
    # The window status should be "partial" because yolo quarantines its
    # agents (quarantine -> track status "partial", not "failed").
    assert result.status == "partial"
    outcomes_by_track = {o.track_id: o for o in result.outcomes}
    assert outcomes_by_track["balanced"].status == "ok"
    assert outcomes_by_track["yolo"].status == "partial"


async def test_replay_reproduces_identical_ids(
    session_factory: sessionmaker[Session],
) -> None:
    """Two runs with identical inputs must produce identical persisted ids."""

    registry = build_registry()

    async def _drive(label: str) -> tuple[str, set[str], set[str], set[str]]:
        result = await run_window(
            registry=registry,
            track_ids=("balanced",),
            symbols=("AAPL", "MSFT"),
            window_label=label,
            config_hash="cfg-replay",
            rng_seed=7,
            now=_FROZEN_NOW,
            llm_factory=_llm_factory,
            session_factory=session_factory,
        )
        track_runs = {o.track_run_id for o in result.outcomes}
        decision_ids = {
            o.result.judge_decision.plan.decision_id
            for o in result.outcomes
            if o.result is not None and o.result.judge_decision is not None
        }
        action_ids = {
            item.action_id
            for o in result.outcomes
            if o.result is not None and o.result.judge_decision is not None
            for item in o.result.judge_decision.plan.items
        }
        return result.run_id, track_runs, decision_ids, action_ids

    run_id_a, trks_a, decs_a, acts_a = await _drive("replay-window")
    run_id_b, trks_b, decs_b, acts_b = await _drive("replay-window")

    assert run_id_a == run_id_b, "window run_id must be deterministic"
    assert trks_a == trks_b, "track_run_ids must be deterministic"
    assert decs_a == decs_b, "decision_ids must be deterministic"
    assert acts_a == acts_b, "action_ids must be deterministic"

    # And the persisted rows should have been upserted (no duplicates).
    with session_factory() as s:
        runs = s.query(Run).filter(Run.run_id == run_id_a).all()
        assert len(runs) == 1
        track_runs = s.query(TrackRun).filter(TrackRun.run_id == run_id_a).all()
        assert {tr.track_run_id for tr in track_runs} == trks_a


async def test_run_window_no_persist_skips_db(
    session_factory: sessionmaker[Session],
) -> None:
    registry = build_registry()
    result = await run_window(
        registry=registry,
        track_ids=("balanced",),
        symbols=("AAPL",),
        window_label="adhoc",
        config_hash="cfg-nopersist",
        now=_FROZEN_NOW,
        llm_factory=_llm_factory,
        persist=False,
    )
    assert result.status == "ok"
    assert len(result.outcomes) == 1
    # No rows should exist in our fixture DB.
    with session_factory() as s:
        assert s.query(Run).filter(Run.run_id == result.run_id).one_or_none() is None


# --------------------------------------------------------------------------- #
# Phase 5 — execution wiring
# --------------------------------------------------------------------------- #
class _BuyingCannedLLM(CannedCouncilLLM):
    """Variant that emits a single small BUY action so the execution
    adapter has something concrete to submit."""

    def _make_judge_decision(self, agent_id: str) -> JudgeDecisionSchema:
        from decimal import Decimal

        from stockripper.agents.schemas import (
            ActionItem,
            ActionOrderType,
            ActionPlan,
            OrderSide,
            PortfolioPosture,
            RecommendationInstrument,
        )

        packet = self._require_packet(agent_id)
        when = self._moment()
        seed = self._stable_id_seed(agent_id, "plan", packet.symbol)
        item = ActionItem(
            action_id=f"act_{seed}",
            track_id=packet.track_id,
            symbol=packet.symbol,
            instrument=RecommendationInstrument.EQUITY,
            side=OrderSide.BUY,
            target_notional_usd=Decimal("500"),
            order_type=ActionOrderType.MARKET,
            rationale=f"[canned-buy] {agent_id}: smoke buy for execution test.",
        )
        plan = ActionPlan(
            decision_id=f"plan_{seed}",
            track_id=packet.track_id,
            judge_agent_id=agent_id,
            judge_agent_version="1.0.0",
            portfolio_posture=PortfolioPosture.NET_LONG,
            items=(item,),
            rationale="[canned-buy] one small BUY for the execution smoke test.",
            objective_label="offline_canned_judge",
            created_at=when,
        )
        return JudgeDecisionSchema(plan=plan, provenance=None)


def _buying_llm_factory(packet: EvidencePacket) -> _BuyingCannedLLM:
    return _BuyingCannedLLM(packet=packet, clock=_FROZEN_NOW)


async def test_run_window_with_execution_adapter_writes_orders_and_fills(
    session_factory: sessionmaker[Session],
) -> None:
    """End-to-end: run a one-track window through the mock execution adapter
    and verify orders + fills land in the ledger, plus the action's
    risk_status is 'approved'."""

    from stockripper.db.models import DecisionAction, Fill, Order
    from stockripper.execution.adapter import (
        ExecutionAdapter,
        MockBrokerClient,
        SubmissionStatus,
    )

    registry = build_registry()
    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    result = await run_window(
        registry=registry,
        track_ids=("balanced",),
        symbols=("AAPL",),
        window_label="opening",
        config_hash="cfg-exec",
        rng_seed=11,
        now=_FROZEN_NOW,
        llm_factory=_buying_llm_factory,
        session_factory=session_factory,
        execution_adapter=adapter,
    )
    assert result.status == "ok"
    assert len(result.submissions) == 1
    sub = result.submissions[0]
    assert sub.status == SubmissionStatus.SUBMITTED
    assert sub.track_id == "balanced"
    assert sub.client_order_id is not None

    with session_factory() as s:
        orders = s.query(Order).all()
        assert len(orders) == 1
        fills = s.query(Fill).all()
        assert len(fills) == 1
        action_rows = s.query(DecisionAction).all()
        assert len(action_rows) == 1
        assert action_rows[0].risk_status == "approved"


async def test_run_window_execution_idempotent_on_replay(
    session_factory: sessionmaker[Session],
) -> None:
    """Re-running the same window with the same inputs collapses onto the
    existing order via deterministic client_order_id."""

    from stockripper.db.models import Order
    from stockripper.execution.adapter import (
        ExecutionAdapter,
        MockBrokerClient,
        SubmissionStatus,
    )

    registry = build_registry()

    async def _drive() -> tuple[str, str | None]:
        adapter = ExecutionAdapter(
            session_factory=session_factory,
            broker=MockBrokerClient(now=_FROZEN_NOW),
            window_id="opening",
        )
        result = await run_window(
            registry=registry,
            track_ids=("balanced",),
            symbols=("AAPL",),
            window_label="opening",
            config_hash="cfg-exec-replay",
            rng_seed=11,
            now=_FROZEN_NOW,
            llm_factory=_buying_llm_factory,
            session_factory=session_factory,
            execution_adapter=adapter,
        )
        sub = result.submissions[0]
        return sub.status.value, sub.client_order_id

    status_a, coid_a = await _drive()
    status_b, coid_b = await _drive()

    assert coid_a == coid_b
    assert status_a == SubmissionStatus.SUBMITTED.value
    assert status_b == SubmissionStatus.DUPLICATE.value

    with session_factory() as s:
        orders = s.query(Order).all()
        assert len(orders) == 1  # Idempotent — no duplicate row.


async def test_run_window_execution_skipped_when_kill_engaged(
    session_factory: sessionmaker[Session],
) -> None:
    """If the kill switch is engaged pre-window, no submissions happen at all."""

    from stockripper.execution.adapter import ExecutionAdapter, MockBrokerClient

    registry = build_registry()
    with session_factory() as s:
        repo = Repository(s)
        repo.engage_kill_switch(reason="ops_drill", engaged_by="test", when=_FROZEN_NOW)
        s.commit()

    adapter = ExecutionAdapter(
        session_factory=session_factory,
        broker=MockBrokerClient(now=_FROZEN_NOW),
        window_id="opening",
    )
    result = await run_window(
        registry=registry,
        track_ids=("balanced",),
        symbols=("AAPL",),
        window_label="opening",
        config_hash="cfg-kill-exec",
        now=_FROZEN_NOW,
        llm_factory=_buying_llm_factory,
        session_factory=session_factory,
        execution_adapter=adapter,
    )
    assert result.status == "aborted_kill"
    assert result.submissions == ()

