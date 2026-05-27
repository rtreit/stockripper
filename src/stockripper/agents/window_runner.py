"""Phase-4 Strategy Tracks Manager.

``run_window`` is the multi-track entry point. It fans out per-track
collaborative cycles in parallel, honors the global kill-switch and
per-track pause state, isolates failures so one bad track does not
kill the window, and persists per-track audit trails transactionally
so a failure inside persistence for one track does not roll back
another track's data.

Spec acceptance (§25 Phase 4): *End-to-end workflow runs all enabled
tracks in parallel with mocked execution; replay reproduces decisions.*
"""

from __future__ import annotations

import asyncio
import datetime as dt
import logging
from dataclasses import dataclass, field
from typing import Final

from sqlalchemy.orm import sessionmaker
from sqlalchemy.orm.session import Session

from stockripper.agents.canned_llm import CannedCouncilLLM
from stockripper.agents.demo import build_demo_packet
from stockripper.agents.ids import (
    packet_id as derive_packet_id,
)
from stockripper.agents.ids import (
    track_run_id as derive_track_run_id,
)
from stockripper.agents.ids import (
    window_run_id as derive_window_run_id,
)
from stockripper.agents.llm import LLMClient
from stockripper.agents.orchestrator import TrackRunResult, run_track
from stockripper.agents.persistence import (
    persist_failed_track,
    persist_skipped_track,
    persist_track_run,
)
from stockripper.agents.registry import AgentRegistry
from stockripper.agents.schemas import EvidencePacket
from stockripper.db.engine import build_session_factory, session_scope
from stockripper.db.repository import Repository
from stockripper.execution.adapter import (
    ExecutionAdapter,
    SubmissionResult,
    SubmissionStatus,
)

LOG: Final = logging.getLogger(__name__)

_KILL_REASON_PREFIX: Final = "kill_switch_engaged"
_PAUSED_REASON_PREFIX: Final = "track_paused"


def _utcnow() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


@dataclass(frozen=True)
class TrackOutcome:
    """One entry in :class:`WindowRunResult.outcomes`."""

    track_id: str
    symbol: str
    packet_id: str
    track_run_id: str
    status: str
    """One of: ok, partial, skipped_paused, aborted_kill, failed."""
    reason: str | None = None
    result: TrackRunResult | None = None
    error: str | None = None


@dataclass(frozen=True)
class WindowRunResult:
    """Aggregate output of one :func:`run_window` call."""

    run_id: str
    window_label: str
    trading_day: dt.date
    started_at: dt.datetime
    completed_at: dt.datetime
    status: str
    """One of: ok, partial, aborted_kill, failed."""
    outcomes: tuple[TrackOutcome, ...] = field(default_factory=tuple)
    submissions: tuple[SubmissionResult, ...] = field(default_factory=tuple)

    @property
    def ok_outcomes(self) -> tuple[TrackOutcome, ...]:
        return tuple(o for o in self.outcomes if o.status == "ok")

    @property
    def skipped_outcomes(self) -> tuple[TrackOutcome, ...]:
        return tuple(o for o in self.outcomes if o.status == "skipped_paused")

    @property
    def aborted_outcomes(self) -> tuple[TrackOutcome, ...]:
        return tuple(o for o in self.outcomes if o.status == "aborted_kill")

    @property
    def failed_outcomes(self) -> tuple[TrackOutcome, ...]:
        return tuple(o for o in self.outcomes if o.status == "failed")


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------
async def run_window(
    *,
    registry: AgentRegistry,
    track_ids: tuple[str, ...],
    symbols: tuple[str, ...],
    window_label: str = "adhoc",
    trading_day: dt.date | None = None,
    config_hash: str = "dev",
    rng_seed: int | None = None,
    now: dt.datetime | None = None,
    llm_factory: LLMClientFactory | None = None,
    persist: bool = True,
    session_factory: sessionmaker[Session] | None = None,
    packet_builder: PacketBuilder | None = None,
    execution_adapter: ExecutionAdapter | None = None,
) -> WindowRunResult:
    """Run every enabled (track, symbol) pair in parallel.

    Parameters
    ----------
    registry:
        Pre-built :class:`AgentRegistry` from :func:`build_registry`.
    track_ids, symbols:
        Cartesian product becomes the (track, symbol) work list.
    window_label, trading_day, config_hash:
        Inputs to the deterministic ``run_id`` derivation. Re-running
        with identical values reuses the same ``run_id`` (upsert).
    rng_seed:
        Threaded into every agent call.
    now:
        Frozen wall-clock for the entire window. **Required for replay
        determinism.** Defaults to ``_utcnow()`` for ad-hoc runs.
    llm_factory:
        Callable that returns a fresh ``LLMClient`` per (track, symbol)
        coroutine. Defaults to a :class:`CannedCouncilLLM` factory so
        the window can run offline. Pass an OpenAI-backed factory for
        live LLM calls.
    persist:
        When True, opens a session via ``session_factory`` (or the
        default factory) and writes window/track/agent rows.
    session_factory:
        Override for tests; defaults to :func:`build_session_factory`.
    packet_builder:
        Override for tests; defaults to a wrapper around
        :func:`build_demo_packet` that injects a deterministic
        ``packet_id``.
    """

    when = now if now is not None else _utcnow()
    day = trading_day if trading_day is not None else when.date()
    run_id = derive_window_run_id(
        window_label=window_label,
        trading_day=day,
        config_hash=config_hash,
        started_at=when,
    )

    factory = llm_factory if llm_factory is not None else _default_canned_factory
    pb = packet_builder if packet_builder is not None else _default_packet_builder

    # Pre-flight: open session for control-plane reads + run row creation.
    paused_track_ids: frozenset[str] = frozenset()
    kill_engaged = False
    kill_reason: str | None = None
    if persist:
        fac = session_factory if session_factory is not None else build_session_factory()
        with session_scope(fac) as session:
            repo = Repository(session)
            repo.create_run(
                run_id=run_id,
                window_label=window_label,
                trading_day=day,
                config_hash=config_hash,
                started_at=when,
            )
            kill = repo.get_kill_switch()
            kill_engaged = kill.engaged
            kill_reason = kill.reason
            paused_track_ids = frozenset(repo.list_paused_track_ids())

    # Build the work list now so we can record skipped/aborted markers
    # for every (track, symbol) pair even when nothing runs.
    work_items: list[_WorkItem] = []
    for track_id in track_ids:
        if track_id not in registry.bindings:
            LOG.warning("run_window: skipping unknown track_id=%s", track_id)
            continue
        for symbol in symbols:
            pid = derive_packet_id(
                track_id=track_id, window_run_id=run_id, symbol=symbol,
            )
            trid = derive_track_run_id(
                window_run_id=run_id, track_id=track_id, packet_id=pid,
            )
            work_items.append(
                _WorkItem(
                    track_id=track_id,
                    symbol=symbol.upper(),
                    packet_id=pid,
                    track_run_id=trid,
                )
            )

    # If the kill-switch is engaged, every track is aborted up front.
    if kill_engaged:
        reason = f"{_KILL_REASON_PREFIX}: {kill_reason or 'unknown'}"
        outcomes = tuple(
            TrackOutcome(
                track_id=w.track_id,
                symbol=w.symbol,
                packet_id=w.packet_id,
                track_run_id=w.track_run_id,
                status="aborted_kill",
                reason=reason,
            )
            for w in work_items
        )
        if persist:
            _persist_skipped_or_aborted(
                outcomes=outcomes,
                run_id=run_id,
                started_at=when,
                session_factory=session_factory,
                final_run_status="aborted_kill",
            )
        return WindowRunResult(
            run_id=run_id,
            window_label=window_label,
            trading_day=day,
            started_at=when,
            completed_at=_utcnow(),
            status="aborted_kill",
            outcomes=outcomes,
        )

    # Build coroutines for every non-paused item; collect markers for
    # paused items separately so they get persisted up-front.
    paused_outcomes: list[TrackOutcome] = []
    runnable: list[_WorkItem] = []
    for w in work_items:
        if w.track_id in paused_track_ids:
            paused_outcomes.append(
                TrackOutcome(
                    track_id=w.track_id,
                    symbol=w.symbol,
                    packet_id=w.packet_id,
                    track_run_id=w.track_run_id,
                    status="skipped_paused",
                    reason=f"{_PAUSED_REASON_PREFIX}: {w.track_id}",
                )
            )
        else:
            runnable.append(w)

    coros = [
        _run_one_track(
            registry=registry,
            run_id=run_id,
            item=w,
            packet_builder=pb,
            llm_factory=factory,
            window_label=window_label,
            when=when,
            rng_seed=rng_seed,
        )
        for w in runnable
    ]
    gathered = await asyncio.gather(*coros, return_exceptions=True)

    run_outcomes: list[TrackOutcome] = []
    for w, raw in zip(runnable, gathered, strict=True):
        if isinstance(raw, BaseException):
            LOG.exception(
                "run_window: track %s/%s raised", w.track_id, w.symbol,
                exc_info=raw,
            )
            run_outcomes.append(
                TrackOutcome(
                    track_id=w.track_id,
                    symbol=w.symbol,
                    packet_id=w.packet_id,
                    track_run_id=w.track_run_id,
                    status="failed",
                    error=repr(raw),
                )
            )
            continue
        result: TrackRunResult = raw
        status = (
            "partial"
            if any(r.status.value == "quarantined" for r in result.all_runs)
            else "ok"
        )
        run_outcomes.append(
            TrackOutcome(
                track_id=w.track_id,
                symbol=w.symbol,
                packet_id=w.packet_id,
                track_run_id=w.track_run_id,
                status=status,
                result=result,
            )
        )

    # Mid-window kill-switch re-check: if engaged between pre-flight and
    # now, mark this window aborted_kill but still keep the outcomes we
    # already produced (they ran before the engage).
    final_kill_engaged = False
    final_kill_reason: str | None = None
    if persist:
        fac2 = session_factory if session_factory is not None else build_session_factory()
        with session_scope(fac2) as session:
            repo = Repository(session)
            kill = repo.get_kill_switch()
            final_kill_engaged = kill.engaged
            final_kill_reason = kill.reason

    all_outcomes = tuple(paused_outcomes + run_outcomes)
    final_status = _aggregate_status(
        all_outcomes,
        post_run_kill_engaged=final_kill_engaged,
        pre_run_kill_engaged=kill_engaged,
    )

    completed_at = _utcnow()

    if persist:
        _persist_window_outcomes(
            outcomes=all_outcomes,
            run_id=run_id,
            started_at=when,
            completed_at=completed_at,
            session_factory=session_factory,
            final_run_status=final_status,
            post_run_kill_reason=final_kill_reason
            if final_kill_engaged and not kill_engaged
            else None,
        )

    submissions: tuple[SubmissionResult, ...] = ()
    if (
        persist
        and execution_adapter is not None
        and final_status != "aborted_kill"
    ):
        submissions = _execute_actions(
            outcomes=all_outcomes,
            adapter=execution_adapter,
        )

    return WindowRunResult(
        run_id=run_id,
        window_label=window_label,
        trading_day=day,
        started_at=when,
        completed_at=completed_at,
        status=final_status,
        outcomes=all_outcomes,
        submissions=submissions,
    )


# ---------------------------------------------------------------------------
# Internals
# ---------------------------------------------------------------------------
from collections.abc import Callable  # noqa: E402  (kept near consumers for clarity)

LLMClientFactory = Callable[[EvidencePacket], LLMClient | None]
PacketBuilder = Callable[..., EvidencePacket]


@dataclass(frozen=True)
class _WorkItem:
    track_id: str
    symbol: str
    packet_id: str
    track_run_id: str


def _default_canned_factory(packet: EvidencePacket) -> LLMClient:
    return CannedCouncilLLM(packet=packet)


def _default_packet_builder(
    *,
    symbol: str,
    track_id: str,
    window_id: str,
    packet_id: str,
    now: dt.datetime,
) -> EvidencePacket:
    return build_demo_packet(
        symbol=symbol,
        track_id=track_id,
        window_id=window_id,
        packet_id=packet_id,
        now=now,
    )


async def _run_one_track(
    *,
    registry: AgentRegistry,
    run_id: str,
    item: _WorkItem,
    packet_builder: PacketBuilder,
    llm_factory: LLMClientFactory,
    window_label: str,
    when: dt.datetime,
    rng_seed: int | None,
) -> TrackRunResult:
    packet = packet_builder(
        symbol=item.symbol,
        track_id=item.track_id,
        window_id=window_label,
        packet_id=item.packet_id,
        now=when,
    )
    llm = llm_factory(packet)
    return await run_track(
        registry=registry,
        track_id=item.track_id,
        packet=packet,
        llm=llm,
        rng_seed=rng_seed,
        window_id=window_label,
        window_run_id=run_id,
        now=when,
    )


def _aggregate_status(
    outcomes: tuple[TrackOutcome, ...],
    *,
    post_run_kill_engaged: bool,
    pre_run_kill_engaged: bool,
) -> str:
    if post_run_kill_engaged and not pre_run_kill_engaged:
        return "aborted_kill"
    if any(o.status == "failed" for o in outcomes):
        return "partial" if any(o.status == "ok" for o in outcomes) else "failed"
    if any(o.status == "partial" for o in outcomes):
        return "partial"
    return "ok"


def _persist_skipped_or_aborted(
    *,
    outcomes: tuple[TrackOutcome, ...],
    run_id: str,
    started_at: dt.datetime,
    session_factory: sessionmaker[Session] | None,
    final_run_status: str,
) -> None:
    fac = session_factory if session_factory is not None else build_session_factory()
    for o in outcomes:
        try:
            with session_scope(fac) as session:
                repo = Repository(session)
                persist_skipped_track(
                    repo,
                    run_id=run_id,
                    track_run_id=o.track_run_id,
                    track_id=o.track_id,
                    packet_id=o.packet_id,
                    symbol=o.symbol,
                    started_at=started_at,
                    reason=o.reason or "unknown",
                    status=o.status,
                )
        except Exception:
            LOG.exception(
                "Failed to persist skipped/aborted marker for %s/%s",
                o.track_id, o.symbol,
            )
    _finalize_run(run_id=run_id, status=final_run_status, session_factory=fac)


def _persist_window_outcomes(
    *,
    outcomes: tuple[TrackOutcome, ...],
    run_id: str,
    started_at: dt.datetime,
    completed_at: dt.datetime,
    session_factory: sessionmaker[Session] | None,
    final_run_status: str,
    post_run_kill_reason: str | None,
) -> None:
    fac = session_factory if session_factory is not None else build_session_factory()
    for o in outcomes:
        try:
            with session_scope(fac) as session:
                repo = Repository(session)
                if o.status == "skipped_paused":
                    persist_skipped_track(
                        repo,
                        run_id=run_id,
                        track_run_id=o.track_run_id,
                        track_id=o.track_id,
                        packet_id=o.packet_id,
                        symbol=o.symbol,
                        started_at=started_at,
                        reason=o.reason or "paused",
                        status="skipped_paused",
                    )
                elif o.status == "failed":
                    persist_failed_track(
                        repo,
                        run_id=run_id,
                        track_run_id=o.track_run_id,
                        track_id=o.track_id,
                        packet_id=o.packet_id,
                        symbol=o.symbol,
                        started_at=started_at,
                        completed_at=completed_at,
                        reason=o.error or "unknown",
                    )
                elif o.result is not None:
                    persist_track_run(
                        repo,
                        run_id=run_id,
                        result=o.result,
                        completed_at=completed_at,
                    )
        except Exception:
            LOG.exception(
                "Failed to persist track run for %s/%s",
                o.track_id, o.symbol,
            )

    notes = post_run_kill_reason
    _finalize_run(
        run_id=run_id,
        status=final_run_status,
        session_factory=fac,
        notes=notes,
        completed_at=completed_at,
    )


def _finalize_run(
    *,
    run_id: str,
    status: str,
    session_factory: sessionmaker[Session],
    notes: str | None = None,
    completed_at: dt.datetime | None = None,
) -> None:
    try:
        with session_scope(session_factory) as session:
            repo = Repository(session)
            run = repo.complete_run(
                run_id=run_id, status=status, completed_at=completed_at,
            )
            if notes is not None:
                run.notes = notes
    except Exception:
        LOG.exception("Failed to finalize run %s", run_id)


def _execute_actions(
    *,
    outcomes: tuple[TrackOutcome, ...],
    adapter: ExecutionAdapter,
) -> tuple[SubmissionResult, ...]:
    """Submit every OK/partial outcome's judge-decision items through the adapter.

    The adapter handles its own kill-switch / pause re-check via the
    universal-floor stack so a kill or pause that lands between
    persistence and submission still blocks the order.
    """

    results: list[SubmissionResult] = []
    for o in outcomes:
        if o.status not in {"ok", "partial"}:
            continue
        if o.result is None or o.result.judge_decision is None:
            continue
        for item in o.result.judge_decision.plan.items:
            try:
                results.append(adapter.submit_action(item))
            except Exception as exc:  # log + continue, never crash window
                LOG.exception(
                    "Execution adapter raised for action %s/%s",
                    o.track_id, item.action_id,
                )
                results.append(
                    SubmissionResult(
                        action_id=item.action_id,
                        track_id=o.track_id,
                        symbol=item.symbol,
                        status=SubmissionStatus.REJECTED_FLOOR,
                        client_order_id=None,
                        local_order_id=None,
                        reason=f"adapter_exception:{exc!r}",
                        risk_status_label="rejected_floor:adapter_exception",
                    )
                )
    return tuple(results)


__all__ = (
    "LLMClientFactory",
    "PacketBuilder",
    "TrackOutcome",
    "WindowRunResult",
    "run_window",
)
