"""Typed repository layer over the SQLAlchemy session.

Each method takes a live :class:`Session` so callers control the transaction
boundary (typically via :func:`stockripper.db.engine.session_scope`). This
keeps the repository free of hidden session lifecycles and makes it trivially
testable.
"""

from __future__ import annotations

import datetime as dt
from decimal import Decimal
from typing import Any

from sqlalchemy import select
from sqlalchemy.orm import Session

from stockripper.db.models import (
    AgentRun,
    AgentScore,
    DecisionAction,
    Fill,
    JudgeDecision,
    JudgeRegretEntry,
    KillSwitchState,
    Order,
    Recommendation,
    RiskPolicy,
    Run,
    StrategyTrack,
    TrackLeaderboardEntry,
    TrackPauseState,
    TrackRun,
    TrackSnapshot,
)


def _utcnow() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


class Repository:
    """Thin facade with the operations Phase-1 callers actually need."""

    def __init__(self, session: Session) -> None:
        self.session = session

    # ------------------------------------------------------------------
    # Risk policies
    # ------------------------------------------------------------------
    def upsert_risk_policy(
        self,
        risk_policy_id: str,
        label: str,
        params: dict[str, Any],
    ) -> RiskPolicy:
        existing = self.session.get(RiskPolicy, risk_policy_id)
        if existing is not None:
            existing.label = label
            existing.params_json = params
            return existing
        policy = RiskPolicy(
            risk_policy_id=risk_policy_id,
            label=label,
            params_json=params,
        )
        self.session.add(policy)
        return policy

    def get_risk_policy(self, risk_policy_id: str) -> RiskPolicy | None:
        return self.session.get(RiskPolicy, risk_policy_id)

    # ------------------------------------------------------------------
    # Strategy tracks
    # ------------------------------------------------------------------
    def upsert_strategy_track(
        self,
        *,
        track_id: str,
        name: str,
        philosophy: str,
        risk_policy_id: str,
        judge_objective: str,
        starting_equity_usd: Decimal,
        enabled: bool = True,
    ) -> StrategyTrack:
        existing = self.session.get(StrategyTrack, track_id)
        if existing is not None:
            existing.name = name
            existing.philosophy = philosophy
            existing.risk_policy_id = risk_policy_id
            existing.judge_objective = judge_objective
            existing.starting_equity_usd = starting_equity_usd
            existing.enabled = enabled
            return existing
        track = StrategyTrack(
            track_id=track_id,
            name=name,
            philosophy=philosophy,
            risk_policy_id=risk_policy_id,
            judge_objective=judge_objective,
            starting_equity_usd=starting_equity_usd,
            enabled=enabled,
        )
        self.session.add(track)
        return track

    def get_strategy_track(self, track_id: str) -> StrategyTrack | None:
        return self.session.get(StrategyTrack, track_id)

    def list_strategy_tracks(
        self, *, enabled_only: bool = False,
    ) -> list[StrategyTrack]:
        stmt = select(StrategyTrack).order_by(StrategyTrack.track_id)
        if enabled_only:
            stmt = stmt.where(StrategyTrack.enabled.is_(True))
        return list(self.session.execute(stmt).scalars())

    # ------------------------------------------------------------------
    # Orders + fills (Phase-1 reconciliation surface)
    # ------------------------------------------------------------------
    def upsert_order_from_alpaca(
        self,
        *,
        track_id: str,
        alpaca_order: dict[str, Any],
    ) -> Order:
        """Reconcile an Alpaca order payload into the local ``orders`` table.

        The Alpaca order's ``id`` becomes ``alpaca_order_id``; the local
        ``client_order_id`` is taken straight from the Alpaca payload so the
        deterministic ID generated at submission time round-trips. The
        ``local_order_id`` uses the same ``client_order_id`` for simplicity
        until we have a dedicated identity scheme.
        """

        client_order_id = str(alpaca_order["client_order_id"])
        existing = self.session.execute(
            select(Order).where(Order.client_order_id == client_order_id)
        ).scalar_one_or_none()

        submitted_at = _parse_dt(alpaca_order.get("submitted_at"))
        defaults: dict[str, Any] = {
            "track_id": track_id,
            "alpaca_order_id": str(alpaca_order["id"]),
            "client_order_id": client_order_id,
            "symbol": str(alpaca_order["symbol"]).upper(),
            "side": str(alpaca_order["side"]),
            "order_type": str(alpaca_order.get("order_type") or alpaca_order.get("type")),
            "time_in_force": str(alpaca_order["time_in_force"]),
            "requested_notional_usd": _to_decimal(alpaca_order.get("notional")),
            "requested_qty": _to_decimal(alpaca_order.get("qty")),
            "limit_price": _to_decimal(alpaca_order.get("limit_price")),
            "stop_price": _to_decimal(alpaca_order.get("stop_price")),
            "status": str(alpaca_order["status"]),
            "submitted_at": submitted_at,
        }

        if existing is None:
            order = Order(local_order_id=client_order_id, **defaults)
            self.session.add(order)
            return order

        for key, value in defaults.items():
            setattr(existing, key, value)
        return existing

    def record_fill(
        self,
        *,
        fill_id: str,
        local_order_id: str,
        filled_qty: Decimal,
        filled_avg_price: Decimal,
        filled_at: dt.datetime,
    ) -> Fill:
        existing = self.session.get(Fill, fill_id)
        if existing is not None:
            existing.filled_qty = filled_qty
            existing.filled_avg_price = filled_avg_price
            existing.filled_at = filled_at
            return existing
        fill = Fill(
            fill_id=fill_id,
            local_order_id=local_order_id,
            filled_qty=filled_qty,
            filled_avg_price=filled_avg_price,
            filled_at=filled_at,
        )
        self.session.add(fill)
        return fill

    # ------------------------------------------------------------------
    # Phase 5 — execution adapter primitives
    # ------------------------------------------------------------------
    def find_order_by_client_order_id(self, client_order_id: str) -> Order | None:
        """Look up an order by its deterministic ``client_order_id``."""

        return self.session.execute(
            select(Order).where(Order.client_order_id == client_order_id)
        ).scalar_one_or_none()

    def upsert_order(
        self,
        *,
        local_order_id: str,
        track_id: str,
        action_id: str | None,
        client_order_id: str,
        symbol: str,
        side: str,
        order_type: str,
        time_in_force: str,
        status: str,
        requested_notional_usd: Decimal | None = None,
        requested_qty: Decimal | None = None,
        limit_price: Decimal | None = None,
        stop_price: Decimal | None = None,
        alpaca_order_id: str | None = None,
        submitted_at: dt.datetime | None = None,
        raw_request_uri: str | None = None,
        raw_response_uri: str | None = None,
    ) -> Order:
        """Insert or update an ``orders`` row keyed by ``client_order_id``.

        Use this from the execution adapter so the second submission of an
        identical intent collapses onto the first row (idempotency).
        """

        existing = self.find_order_by_client_order_id(client_order_id)
        defaults: dict[str, Any] = {
            "action_id": action_id,
            "track_id": track_id,
            "alpaca_order_id": alpaca_order_id,
            "client_order_id": client_order_id,
            "symbol": symbol.upper(),
            "side": side,
            "order_type": order_type,
            "time_in_force": time_in_force,
            "requested_notional_usd": requested_notional_usd,
            "requested_qty": requested_qty,
            "limit_price": limit_price,
            "stop_price": stop_price,
            "status": status,
            "submitted_at": submitted_at,
            "raw_request_uri": raw_request_uri,
            "raw_response_uri": raw_response_uri,
        }
        if existing is None:
            order = Order(local_order_id=local_order_id, **defaults)
            self.session.add(order)
            self.session.flush()
            return order
        for key, value in defaults.items():
            setattr(existing, key, value)
        self.session.flush()
        return existing

    def list_orders_for_track(self, *, track_id: str) -> list[Order]:
        stmt = (
            select(Order)
            .where(Order.track_id == track_id)
            .order_by(Order.submitted_at.desc().nullslast())
        )
        return list(self.session.execute(stmt).scalars())

    def set_action_risk_status(
        self, *, action_id: str, risk_status: str,
    ) -> DecisionAction | None:
        """Set ``decision_actions.risk_status`` for a single action."""

        existing = self.session.get(DecisionAction, action_id)
        if existing is None:
            return None
        existing.risk_status = risk_status
        self.session.flush()
        return existing

    # ------------------------------------------------------------------
    # Track snapshots (reconciliation output)
    # ------------------------------------------------------------------
    def record_track_snapshot(
        self,
        *,
        snapshot_id: str,
        track_id: str,
        captured_at: dt.datetime,
        equity: Decimal,
        cash: Decimal,
        buying_power: Decimal | None = None,
        gross_exposure: Decimal | None = None,
        net_exposure: Decimal | None = None,
        short_exposure: Decimal | None = None,
        options_notional: Decimal | None = None,
        run_id: str | None = None,
        raw_snapshot_uri: str | None = None,
    ) -> TrackSnapshot:
        existing = self.session.get(TrackSnapshot, snapshot_id)
        if existing is not None:
            existing.captured_at = captured_at
            existing.equity = equity
            existing.cash = cash
            existing.buying_power = buying_power
            existing.gross_exposure = gross_exposure
            existing.net_exposure = net_exposure
            existing.short_exposure = short_exposure
            existing.options_notional = options_notional
            existing.run_id = run_id
            existing.raw_snapshot_uri = raw_snapshot_uri
            return existing
        snap = TrackSnapshot(
            snapshot_id=snapshot_id,
            run_id=run_id,
            track_id=track_id,
            captured_at=captured_at,
            equity=equity,
            cash=cash,
            buying_power=buying_power,
            gross_exposure=gross_exposure,
            net_exposure=net_exposure,
            short_exposure=short_exposure,
            options_notional=options_notional,
            raw_snapshot_uri=raw_snapshot_uri,
        )
        self.session.add(snap)
        return snap

    def latest_track_snapshot(self, track_id: str) -> TrackSnapshot | None:
        stmt = (
            select(TrackSnapshot)
            .where(TrackSnapshot.track_id == track_id)
            .order_by(TrackSnapshot.captured_at.desc())
            .limit(1)
        )
        return self.session.execute(stmt).scalar_one_or_none()

    # ------------------------------------------------------------------
    # Phase 4 — window run + track/agent audit
    # ------------------------------------------------------------------
    def create_run(
        self,
        *,
        run_id: str,
        window_label: str,
        trading_day: dt.date,
        config_hash: str,
        started_at: dt.datetime,
        status: str = "running",
        notes: str | None = None,
    ) -> Run:
        existing = self.session.get(Run, run_id)
        if existing is not None:
            return existing
        run = Run(
            run_id=run_id,
            window_label=window_label,
            trading_day=trading_day,
            config_hash=config_hash,
            started_at=started_at,
            status=status,
            notes=notes,
        )
        self.session.add(run)
        self.session.flush()
        return run

    def complete_run(
        self,
        *,
        run_id: str,
        status: str,
        completed_at: dt.datetime | None = None,
    ) -> Run:
        run = self.session.get(Run, run_id)
        if run is None:
            raise KeyError(f"unknown run_id: {run_id!r}")
        run.status = status
        run.completed_at = completed_at if completed_at is not None else _utcnow()
        self.session.flush()
        return run

    def get_run(self, run_id: str) -> Run | None:
        return self.session.get(Run, run_id)

    def upsert_track_run(
        self,
        *,
        track_run_id: str,
        run_id: str,
        track_id: str,
        packet_id: str,
        symbol: str,
        status: str,
        started_at: dt.datetime,
        completed_at: dt.datetime | None = None,
        interrupt_reason: str | None = None,
    ) -> TrackRun:
        existing = self.session.get(TrackRun, track_run_id)
        if existing is not None:
            existing.status = status
            existing.completed_at = completed_at
            existing.interrupt_reason = interrupt_reason
            return existing
        track_run = TrackRun(
            track_run_id=track_run_id,
            run_id=run_id,
            track_id=track_id,
            packet_id=packet_id,
            symbol=symbol,
            status=status,
            started_at=started_at,
            completed_at=completed_at,
            interrupt_reason=interrupt_reason,
        )
        self.session.add(track_run)
        self.session.flush()
        return track_run

    def list_track_runs(self, *, run_id: str) -> list[TrackRun]:
        stmt = (
            select(TrackRun)
            .where(TrackRun.run_id == run_id)
            .order_by(TrackRun.track_id, TrackRun.packet_id)
        )
        return list(self.session.execute(stmt).scalars())

    def upsert_agent_run(
        self,
        *,
        agent_run_id: str,
        track_run_id: str,
        run_id: str,
        track_id: str,
        agent_id: str,
        agent_version: str,
        output_schema_name: str,
        status: str,
        fingerprint_digest: str,
        model_id: str,
        seed: int | None,
        prompt_content_hash: str,
        schema_content_hash: str,
        input_content_hash: str,
        output_json: dict[str, Any] | None,
        raw_response_text: str | None,
        quarantine_reason: str | None,
        latency_ms: int | None,
        started_at: dt.datetime,
        created_at: dt.datetime,
    ) -> AgentRun:
        existing = self.session.get(AgentRun, agent_run_id)
        if existing is not None:
            existing.status = status
            existing.output_json = output_json
            existing.raw_response_text = raw_response_text
            existing.quarantine_reason = quarantine_reason
            existing.latency_ms = latency_ms
            existing.created_at = created_at
            return existing
        agent_run = AgentRun(
            agent_run_id=agent_run_id,
            track_run_id=track_run_id,
            run_id=run_id,
            track_id=track_id,
            agent_id=agent_id,
            agent_version=agent_version,
            output_schema_name=output_schema_name,
            status=status,
            fingerprint_digest=fingerprint_digest,
            model_id=model_id,
            seed=seed,
            prompt_content_hash=prompt_content_hash,
            schema_content_hash=schema_content_hash,
            input_content_hash=input_content_hash,
            output_json=output_json,
            raw_response_text=raw_response_text,
            quarantine_reason=quarantine_reason,
            latency_ms=latency_ms,
            started_at=started_at,
            created_at=created_at,
        )
        self.session.add(agent_run)
        self.session.flush()
        return agent_run

    def upsert_recommendation(self, **values: Any) -> Recommendation:
        rid = str(values["recommendation_id"])
        existing = self.session.get(Recommendation, rid)
        if existing is not None:
            for k, v in values.items():
                setattr(existing, k, v)
            return existing
        rec = Recommendation(**values)
        self.session.add(rec)
        self.session.flush()
        return rec

    def upsert_judge_decision(self, **values: Any) -> JudgeDecision:
        did = str(values["decision_id"])
        existing = self.session.get(JudgeDecision, did)
        if existing is not None:
            for k, v in values.items():
                setattr(existing, k, v)
            return existing
        decision = JudgeDecision(**values)
        self.session.add(decision)
        self.session.flush()
        return decision

    def upsert_decision_action(self, **values: Any) -> DecisionAction:
        aid = str(values["action_id"])
        existing = self.session.get(DecisionAction, aid)
        if existing is not None:
            for k, v in values.items():
                setattr(existing, k, v)
            return existing
        action = DecisionAction(**values)
        self.session.add(action)
        self.session.flush()
        return action

    # ------------------------------------------------------------------
    # Phase 4 — control-plane state (kill switch + per-track pause)
    # ------------------------------------------------------------------
    def get_kill_switch(self) -> KillSwitchState:
        existing = self.session.get(KillSwitchState, 1)
        if existing is not None:
            return existing
        row = KillSwitchState(
            id=1, engaged=False, reason=None, engaged_at=None, engaged_by=None,
            updated_at=_utcnow(),
        )
        self.session.add(row)
        self.session.flush()
        return row

    def engage_kill_switch(
        self, *, reason: str, engaged_by: str | None = None,
        when: dt.datetime | None = None,
    ) -> KillSwitchState:
        state = self.get_kill_switch()
        moment = when if when is not None else _utcnow()
        state.engaged = True
        state.reason = reason
        state.engaged_at = moment
        state.engaged_by = engaged_by
        state.updated_at = moment
        self.session.flush()
        return state

    def release_kill_switch(
        self, *, when: dt.datetime | None = None,
    ) -> KillSwitchState:
        state = self.get_kill_switch()
        moment = when if when is not None else _utcnow()
        state.engaged = False
        state.reason = None
        state.engaged_at = None
        state.engaged_by = None
        state.updated_at = moment
        self.session.flush()
        return state

    def get_track_pause(self, track_id: str) -> TrackPauseState | None:
        return self.session.get(TrackPauseState, track_id)

    def pause_track(
        self, *, track_id: str, reason: str,
        paused_by: str | None = None,
        when: dt.datetime | None = None,
    ) -> TrackPauseState:
        moment = when if when is not None else _utcnow()
        existing = self.session.get(TrackPauseState, track_id)
        if existing is not None:
            existing.paused = True
            existing.reason = reason
            existing.paused_at = moment
            existing.paused_by = paused_by
            existing.updated_at = moment
            self.session.flush()
            return existing
        row = TrackPauseState(
            track_id=track_id,
            paused=True,
            reason=reason,
            paused_at=moment,
            paused_by=paused_by,
            updated_at=moment,
        )
        self.session.add(row)
        self.session.flush()
        return row

    def resume_track(
        self, *, track_id: str,
        when: dt.datetime | None = None,
    ) -> TrackPauseState:
        moment = when if when is not None else _utcnow()
        existing = self.session.get(TrackPauseState, track_id)
        if existing is None:
            row = TrackPauseState(
                track_id=track_id,
                paused=False,
                reason=None,
                paused_at=None,
                paused_by=None,
                updated_at=moment,
            )
            self.session.add(row)
            self.session.flush()
            return row
        existing.paused = False
        existing.reason = None
        existing.paused_at = None
        existing.paused_by = None
        existing.updated_at = moment
        self.session.flush()
        return existing

    def list_paused_track_ids(self) -> list[str]:
        stmt = (
            select(TrackPauseState.track_id)
            .where(TrackPauseState.paused.is_(True))
            .order_by(TrackPauseState.track_id)
        )
        return list(self.session.execute(stmt).scalars())

    def list_track_pause_states(self) -> list[TrackPauseState]:
        stmt = select(TrackPauseState).order_by(TrackPauseState.track_id)
        return list(self.session.execute(stmt).scalars())

    # ------------------------------------------------------------------
    # Phase 6 — scoring (agent_scores, track_leaderboard, judge_regret)
    # ------------------------------------------------------------------
    def upsert_agent_score(
        self,
        *,
        score_id: str,
        agent_id: str,
        track_id: str,
        as_of_date: dt.date,
        reward_score: Decimal,
        observation_count: int,
        calibration_score: Decimal | None = None,
        evidence_quality_score: Decimal | None = None,
        shadow_return_pct: Decimal | None = None,
        selected_return_pct: Decimal | None = None,
    ) -> AgentScore:
        existing = self.session.get(AgentScore, score_id)
        if existing is not None:
            existing.reward_score = reward_score
            existing.calibration_score = calibration_score
            existing.evidence_quality_score = evidence_quality_score
            existing.shadow_return_pct = shadow_return_pct
            existing.selected_return_pct = selected_return_pct
            existing.observation_count = observation_count
            self.session.flush()
            return existing
        row = AgentScore(
            score_id=score_id,
            agent_id=agent_id,
            track_id=track_id,
            as_of_date=as_of_date,
            reward_score=reward_score,
            calibration_score=calibration_score,
            evidence_quality_score=evidence_quality_score,
            shadow_return_pct=shadow_return_pct,
            selected_return_pct=selected_return_pct,
            observation_count=observation_count,
        )
        self.session.add(row)
        self.session.flush()
        return row

    def list_agent_scores(
        self, *, track_id: str | None = None,
        agent_id: str | None = None,
        as_of_date: dt.date | None = None,
    ) -> list[AgentScore]:
        stmt = select(AgentScore)
        if track_id is not None:
            stmt = stmt.where(AgentScore.track_id == track_id)
        if agent_id is not None:
            stmt = stmt.where(AgentScore.agent_id == agent_id)
        if as_of_date is not None:
            stmt = stmt.where(AgentScore.as_of_date == as_of_date)
        stmt = stmt.order_by(
            AgentScore.as_of_date.desc(),
            AgentScore.reward_score.desc(),
        )
        return list(self.session.execute(stmt).scalars())

    def upsert_leaderboard_entry(
        self,
        *,
        leaderboard_id: str,
        window_start: dt.date,
        window_end: dt.date,
        track_id: str,
        cumulative_return_pct: Decimal | None = None,
        sharpe: Decimal | None = None,
        sortino: Decimal | None = None,
        calmar: Decimal | None = None,
        max_drawdown_pct: Decimal | None = None,
        win_rate: Decimal | None = None,
        turnover: Decimal | None = None,
        rank: int | None = None,
    ) -> TrackLeaderboardEntry:
        existing = self.session.get(TrackLeaderboardEntry, leaderboard_id)
        if existing is not None:
            existing.cumulative_return_pct = cumulative_return_pct
            existing.sharpe = sharpe
            existing.sortino = sortino
            existing.calmar = calmar
            existing.max_drawdown_pct = max_drawdown_pct
            existing.win_rate = win_rate
            existing.turnover = turnover
            existing.rank = rank
            self.session.flush()
            return existing
        row = TrackLeaderboardEntry(
            leaderboard_id=leaderboard_id,
            window_start=window_start,
            window_end=window_end,
            track_id=track_id,
            cumulative_return_pct=cumulative_return_pct,
            sharpe=sharpe,
            sortino=sortino,
            calmar=calmar,
            max_drawdown_pct=max_drawdown_pct,
            win_rate=win_rate,
            turnover=turnover,
            rank=rank,
        )
        self.session.add(row)
        self.session.flush()
        return row

    def list_leaderboard(
        self,
        *,
        window_start: dt.date | None = None,
        window_end: dt.date | None = None,
    ) -> list[TrackLeaderboardEntry]:
        stmt = select(TrackLeaderboardEntry)
        if window_start is not None:
            stmt = stmt.where(TrackLeaderboardEntry.window_start == window_start)
        if window_end is not None:
            stmt = stmt.where(TrackLeaderboardEntry.window_end == window_end)
        stmt = stmt.order_by(
            TrackLeaderboardEntry.window_end.desc(),
            TrackLeaderboardEntry.rank.asc().nullslast(),
        )
        return list(self.session.execute(stmt).scalars())

    def upsert_judge_regret(
        self,
        *,
        regret_id: str,
        judge_agent_id: str,
        track_id: str,
        as_of_date: dt.date,
        selected_reward: Decimal,
        best_alternative_reward: Decimal,
        regret: Decimal,
        observation_count: int,
    ) -> JudgeRegretEntry:
        existing = self.session.get(JudgeRegretEntry, regret_id)
        if existing is not None:
            existing.selected_reward = selected_reward
            existing.best_alternative_reward = best_alternative_reward
            existing.regret = regret
            existing.observation_count = observation_count
            self.session.flush()
            return existing
        row = JudgeRegretEntry(
            regret_id=regret_id,
            judge_agent_id=judge_agent_id,
            track_id=track_id,
            as_of_date=as_of_date,
            selected_reward=selected_reward,
            best_alternative_reward=best_alternative_reward,
            regret=regret,
            observation_count=observation_count,
        )
        self.session.add(row)
        self.session.flush()
        return row

    def list_judge_regret(
        self, *, track_id: str | None = None,
    ) -> list[JudgeRegretEntry]:
        stmt = select(JudgeRegretEntry)
        if track_id is not None:
            stmt = stmt.where(JudgeRegretEntry.track_id == track_id)
        stmt = stmt.order_by(JudgeRegretEntry.as_of_date.desc())
        return list(self.session.execute(stmt).scalars())

    def list_recommendations(
        self,
        *,
        run_id: str | None = None,
        track_id: str | None = None,
        agent_id: str | None = None,
    ) -> list[Recommendation]:
        stmt = select(Recommendation)
        if run_id is not None:
            stmt = stmt.where(Recommendation.run_id == run_id)
        if track_id is not None:
            stmt = stmt.where(Recommendation.track_id == track_id)
        if agent_id is not None:
            stmt = stmt.where(Recommendation.agent_id == agent_id)
        stmt = stmt.order_by(Recommendation.created_at.desc())
        return list(self.session.execute(stmt).scalars())

    def list_runs(self, *, limit: int = 50) -> list[Run]:
        stmt = select(Run).order_by(Run.started_at.desc()).limit(limit)
        return list(self.session.execute(stmt).scalars())


# ----------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------
def _to_decimal(value: Any) -> Decimal | None:
    if value is None or value == "":
        return None
    if isinstance(value, Decimal):
        return value
    return Decimal(str(value))


def _parse_dt(value: Any) -> dt.datetime | None:
    if value is None:
        return None
    if isinstance(value, dt.datetime):
        return value if value.tzinfo else value.replace(tzinfo=dt.UTC)
    text = str(value).replace("Z", "+00:00")
    parsed = dt.datetime.fromisoformat(text)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=dt.UTC)
