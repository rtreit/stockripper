"""FastAPI dashboard app (spec §19)."""

from __future__ import annotations

import asyncio
import datetime as dt
import json
from collections.abc import Callable, Iterable
from contextlib import asynccontextmanager
from decimal import Decimal
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException, Query, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles
from sqlalchemy.orm import Session, sessionmaker

from stockripper.dashboard.events import DashboardEvent, EventBus
from stockripper.db.repository import Repository

SessionFactory = Callable[[], Session]


def _to_jsonable(value: Any) -> Any:
    if isinstance(value, Decimal):
        return str(value)
    if isinstance(value, (dt.datetime, dt.date)):
        return value.isoformat()
    return value


def _serialize_run(run: Any) -> dict[str, Any]:
    return {
        "run_id": run.run_id,
        "experiment_id": run.experiment_id,
        "trading_day": _to_jsonable(run.trading_day),
        "window_label": run.window_label,
        "status": run.status,
        "started_at": _to_jsonable(run.started_at),
        "completed_at": _to_jsonable(run.completed_at),
        "config_hash": run.config_hash,
        "notes": run.notes,
    }


def _serialize_track(track: Any) -> dict[str, Any]:
    return {
        "track_id": track.track_id,
        "name": track.name,
        "philosophy": track.philosophy,
        "risk_policy_id": track.risk_policy_id,
        "judge_objective": track.judge_objective,
        "enabled": track.enabled,
        "starting_equity_usd": _to_jsonable(track.starting_equity_usd),
    }


def _serialize_recommendation(rec: Any) -> dict[str, Any]:
    return {
        "recommendation_id": rec.recommendation_id,
        "run_id": rec.run_id,
        "track_id": rec.track_id,
        "agent_id": rec.agent_id,
        "symbol": rec.symbol,
        "instrument_type": rec.instrument_type,
        "action": rec.action,
        "conviction": _to_jsonable(rec.conviction),
        "time_horizon_days": rec.time_horizon_days,
        "expected_return_pct": _to_jsonable(rec.expected_return_pct),
        "thesis": rec.thesis,
        "created_at": _to_jsonable(rec.created_at),
    }


def _serialize_leaderboard_entry(row: Any) -> dict[str, Any]:
    return {
        "leaderboard_id": row.leaderboard_id,
        "window_start": _to_jsonable(row.window_start),
        "window_end": _to_jsonable(row.window_end),
        "track_id": row.track_id,
        "cumulative_return_pct": _to_jsonable(row.cumulative_return_pct),
        "sharpe": _to_jsonable(row.sharpe),
        "sortino": _to_jsonable(row.sortino),
        "calmar": _to_jsonable(row.calmar),
        "max_drawdown_pct": _to_jsonable(row.max_drawdown_pct),
        "win_rate": _to_jsonable(row.win_rate),
        "turnover": _to_jsonable(row.turnover),
        "rank": row.rank,
    }


def _serialize_agent_score(row: Any) -> dict[str, Any]:
    return {
        "score_id": row.score_id,
        "agent_id": row.agent_id,
        "track_id": row.track_id,
        "as_of_date": _to_jsonable(row.as_of_date),
        "reward_score": _to_jsonable(row.reward_score),
        "selected_return_pct": _to_jsonable(row.selected_return_pct),
        "shadow_return_pct": _to_jsonable(row.shadow_return_pct),
        "observation_count": row.observation_count,
    }


def _serialize_judge_regret(row: Any) -> dict[str, Any]:
    return {
        "regret_id": row.regret_id,
        "judge_agent_id": row.judge_agent_id,
        "track_id": row.track_id,
        "as_of_date": _to_jsonable(row.as_of_date),
        "selected_reward": _to_jsonable(row.selected_reward),
        "best_alternative_reward": _to_jsonable(row.best_alternative_reward),
        "regret": _to_jsonable(row.regret),
        "observation_count": row.observation_count,
    }


def _parse_date(value: str | None) -> dt.date | None:
    if value is None:
        return None
    try:
        return dt.date.fromisoformat(value)
    except ValueError as exc:
        raise HTTPException(
            status_code=400, detail=f"Invalid date: {value!r}",
        ) from exc


def _static_dir() -> Path:
    return Path(__file__).resolve().parent / "static"


def build_app(
    *,
    session_factory: SessionFactory | sessionmaker[Session],
    event_bus: EventBus | None = None,
) -> FastAPI:
    """Construct a FastAPI dashboard app.

    ``session_factory`` may be a vanilla callable returning a Session or
    a SQLAlchemy ``sessionmaker``; both are callable with no args.
    """

    bus = event_bus or EventBus()

    @asynccontextmanager
    async def lifespan(_app: FastAPI) -> Any:
        try:
            yield
        finally:
            await bus.close()

    app = FastAPI(
        title="StockRipper Dashboard",
        version="0.6.0",
        lifespan=lifespan,
    )

    def _open_session() -> Session:
        return session_factory()

    @app.get("/api/health")
    def health() -> dict[str, Any]:
        return {"status": "ok", "subscribers": bus.subscriber_count}

    @app.get("/api/runs")
    def list_runs(limit: int = Query(default=50, ge=1, le=500)) -> JSONResponse:
        with _open_session() as session:
            repo = Repository(session)
            runs = repo.list_runs(limit=limit)
            return JSONResponse([_serialize_run(r) for r in runs])

    @app.get("/api/runs/{run_id}")
    def get_run(run_id: str) -> JSONResponse:
        with _open_session() as session:
            repo = Repository(session)
            run = repo.get_run(run_id)
            if run is None:
                raise HTTPException(status_code=404, detail="run not found")
            recs = repo.list_recommendations(run_id=run_id)
            return JSONResponse(
                {
                    "run": _serialize_run(run),
                    "recommendations": [
                        _serialize_recommendation(r) for r in recs
                    ],
                },
            )

    @app.get("/api/tracks")
    def list_tracks() -> JSONResponse:
        with _open_session() as session:
            repo = Repository(session)
            tracks = repo.list_strategy_tracks()
            return JSONResponse([_serialize_track(t) for t in tracks])

    @app.get("/api/leaderboard")
    def leaderboard(
        window_start: str | None = Query(default=None),
        window_end: str | None = Query(default=None),
    ) -> JSONResponse:
        ws = _parse_date(window_start)
        we = _parse_date(window_end)
        with _open_session() as session:
            repo = Repository(session)
            rows = repo.list_leaderboard(window_start=ws, window_end=we)
            return JSONResponse(
                [_serialize_leaderboard_entry(r) for r in rows],
            )

    @app.get("/api/agent-scores")
    def agent_scores(
        track_id: str | None = Query(default=None),
        agent_id: str | None = Query(default=None),
        as_of_date: str | None = Query(default=None),
    ) -> JSONResponse:
        with _open_session() as session:
            repo = Repository(session)
            rows = repo.list_agent_scores(
                track_id=track_id,
                agent_id=agent_id,
                as_of_date=_parse_date(as_of_date),
            )
            return JSONResponse(
                [_serialize_agent_score(r) for r in rows],
            )

    @app.get("/api/judge-regret")
    def judge_regret(
        track_id: str | None = Query(default=None),
    ) -> JSONResponse:
        with _open_session() as session:
            repo = Repository(session)
            rows = repo.list_judge_regret(track_id=track_id)
            return JSONResponse(
                [_serialize_judge_regret(r) for r in rows],
            )

    @app.post("/api/events", status_code=202)
    async def push_event(request: Request) -> dict[str, Any]:
        """Accept a DashboardEvent from a remote orchestrator process.

        Used by ``HttpEventEmitter`` so a separate ``run-window`` process
        can light up this dashboard's Live Council Feed.
        """

        try:
            body = await request.json()
        except json.JSONDecodeError as exc:
            raise HTTPException(status_code=400, detail="invalid json") from exc
        try:
            event = DashboardEvent.model_validate(body)
        except Exception as exc:
            raise HTTPException(status_code=422, detail=str(exc)) from exc
        await bus.publish(event)
        return {"accepted": True, "event": event.event}

    @app.websocket("/ws/events")
    async def ws_events(websocket: WebSocket) -> None:
        await websocket.accept()
        try:
            async for event in bus.subscribe():
                await websocket.send_text(
                    event.model_dump_json(),
                )
        except WebSocketDisconnect:
            return
        except asyncio.CancelledError:  # pragma: no cover — shutdown
            return

    # Mount static SPA last so /api/* routes win.
    static_dir = _static_dir()
    if static_dir.exists():
        app.mount(
            "/", StaticFiles(directory=str(static_dir), html=True), name="ui",
        )

    return app


def app_event_bus(app: FastAPI) -> EventBus | None:
    """Walk app dependencies to find the wired :class:`EventBus`."""

    # FastAPI doesn't expose the lifespan-bound closures; consumers
    # should pass the EventBus into build_app rather than fishing for it
    # here. Kept as a hook for future introspection tooling.
    _ = app
    return None


def _serialize_iter(rows: Iterable[Any]) -> str:
    return json.dumps([_to_jsonable(r) for r in rows], default=str)


__all__ = ("build_app",)
