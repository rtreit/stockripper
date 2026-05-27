"""Phase-4 persistence translator.

Converts a :class:`TrackRunResult` produced by ``run_track`` into rows in
``track_runs`` / ``agent_runs`` / ``recommendations`` / ``judge_decisions``
/ ``decision_actions`` tables.

The window runner (``run_window``) calls :func:`persist_track_run` once
per completed (or skipped) track inside its own transaction so a failure
on one track does not roll back another track's audit trail.
"""

from __future__ import annotations

import datetime as dt
from typing import Any

from stockripper.agents.orchestrator import TrackRunResult
from stockripper.agents.schemas import (
    ActionPlan,
    AgentRecommendation,
    AgentRunResult,
    AgentRunStatus,
    JudgeDecision,
    action_item_to_ledger_row,
    recommendation_to_ledger_row,
)
from stockripper.db.repository import Repository


def _output_schema_name(result: AgentRunResult) -> str:
    if result.output is not None:
        return type(result.output).__name__
    return "Unknown"


def _output_json(result: AgentRunResult) -> dict[str, Any] | None:
    if result.output is None:
        return None
    return result.output.model_dump(mode="json")


def persist_track_run(
    repo: Repository,
    *,
    run_id: str,
    result: TrackRunResult,
    completed_at: dt.datetime,
) -> None:
    """Persist a successful (or partial) track run.

    Writes the TrackRun envelope, one AgentRun per envelope, recommendation
    rows for OK council outputs, and judge decision + decision actions when
    the judge ran successfully. Idempotent: re-running with identical
    deterministic ids upserts existing rows.

    Caller controls the transaction boundary; this function only stages
    inserts/updates on the session.
    """

    has_quarantine = any(
        r.status == AgentRunStatus.QUARANTINED for r in result.all_runs
    )
    track_run_status = "partial" if has_quarantine else "ok"

    repo.upsert_track_run(
        track_run_id=result.track_run_id,
        run_id=run_id,
        track_id=result.track_id,
        packet_id=result.packet.packet_id,
        symbol=result.packet.symbol,
        status=track_run_status,
        started_at=result.started_at,
        completed_at=completed_at,
    )

    for envelope in result.all_runs:
        _persist_agent_run(
            repo,
            run_id=run_id,
            track_run_id=result.track_run_id,
            track_id=result.track_id,
            envelope=envelope,
        )

    for envelope in result.council_runs:
        if envelope.status == AgentRunStatus.OK and isinstance(
            envelope.output, AgentRecommendation
        ):
            row = recommendation_to_ledger_row(envelope.output)
            row["run_id"] = run_id
            repo.upsert_recommendation(**row)

    decision = result.judge_decision
    if decision is not None:
        _persist_judge_decision(repo, run_id=run_id, decision=decision)


def persist_skipped_track(
    repo: Repository,
    *,
    run_id: str,
    track_run_id: str,
    track_id: str,
    packet_id: str,
    symbol: str,
    started_at: dt.datetime,
    reason: str,
    status: str = "skipped_paused",
) -> None:
    """Write a TrackRun marker for a (track, packet) that did not execute."""

    repo.upsert_track_run(
        track_run_id=track_run_id,
        run_id=run_id,
        track_id=track_id,
        packet_id=packet_id,
        symbol=symbol,
        status=status,
        started_at=started_at,
        completed_at=started_at,
        interrupt_reason=reason,
    )


def persist_failed_track(
    repo: Repository,
    *,
    run_id: str,
    track_run_id: str,
    track_id: str,
    packet_id: str,
    symbol: str,
    started_at: dt.datetime,
    completed_at: dt.datetime,
    reason: str,
) -> None:
    """Write a TrackRun marker when ``run_track`` itself raised."""

    repo.upsert_track_run(
        track_run_id=track_run_id,
        run_id=run_id,
        track_id=track_id,
        packet_id=packet_id,
        symbol=symbol,
        status="failed",
        started_at=started_at,
        completed_at=completed_at,
        interrupt_reason=reason,
    )


# ---------------------------------------------------------------------------
# Internals
# ---------------------------------------------------------------------------
def _persist_agent_run(
    repo: Repository,
    *,
    run_id: str,
    track_run_id: str,
    track_id: str,
    envelope: AgentRunResult,
) -> None:
    fingerprint = envelope.fingerprint
    repo.upsert_agent_run(
        agent_run_id=envelope.run_id,
        track_run_id=track_run_id,
        run_id=run_id,
        track_id=track_id,
        agent_id=envelope.agent_id,
        agent_version=envelope.agent_version,
        output_schema_name=_output_schema_name(envelope),
        status=envelope.status.value,
        fingerprint_digest=fingerprint.digest,
        model_id=fingerprint.model_id,
        seed=fingerprint.seed,
        prompt_content_hash=fingerprint.prompt_content_hash,
        schema_content_hash=fingerprint.schema_content_hash,
        input_content_hash=fingerprint.input_content_hash,
        output_json=_output_json(envelope),
        raw_response_text=envelope.raw_response_text,
        quarantine_reason=envelope.quarantine_reason,
        latency_ms=envelope.latency_ms,
        started_at=envelope.created_at,
        created_at=envelope.created_at,
    )


def _persist_judge_decision(
    repo: Repository,
    *,
    run_id: str,
    decision: JudgeDecision,
) -> None:
    plan: ActionPlan = decision.plan
    repo.upsert_judge_decision(
        decision_id=plan.decision_id,
        run_id=run_id,
        track_id=plan.track_id,
        judge_agent_id=plan.judge_agent_id,
        portfolio_posture=plan.portfolio_posture.value,
        raw_output_uri=None,
        created_at=plan.created_at,
    )
    for item in plan.items:
        row = action_item_to_ledger_row(item, decision_id=plan.decision_id)
        # ``decision_actions`` does not have a ``risk_status`` value yet
        # (Phase 5 will populate it from the risk gate); leave it null.
        repo.upsert_decision_action(**row)


__all__ = (
    "persist_failed_track",
    "persist_skipped_track",
    "persist_track_run",
)
