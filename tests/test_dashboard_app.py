"""Tests for the FastAPI dashboard app."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.orm import Session, sessionmaker

from stockripper.dashboard import build_app
from stockripper.dashboard.events import EventBus
from stockripper.db import Base, Repository, build_engine
from stockripper.tracks import seed_default_tracks


@pytest.fixture
def session_factory(tmp_path: Path) -> sessionmaker[Session]:
    db = tmp_path / "dashboard.sqlite"
    engine = build_engine(f"sqlite:///{db}")
    Base.metadata.create_all(bind=engine)
    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    with factory() as s:
        seed_default_tracks(s)
        s.commit()
    return factory


@pytest.fixture
def event_bus() -> EventBus:
    return EventBus()


@pytest.fixture
def client(
    session_factory: sessionmaker[Session], event_bus: EventBus,
) -> TestClient:
    app = build_app(session_factory=session_factory, event_bus=event_bus)
    return TestClient(app)


def test_health_endpoint(client: TestClient) -> None:
    resp = client.get("/api/health")
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] == "ok"
    assert "subscribers" in body


def test_list_tracks_returns_seeded_tracks(client: TestClient) -> None:
    resp = client.get("/api/tracks")
    assert resp.status_code == 200
    rows = resp.json()
    assert len(rows) >= 1
    assert "track_id" in rows[0]


def test_runs_list_initially_empty(client: TestClient) -> None:
    resp = client.get("/api/runs")
    assert resp.status_code == 200
    assert resp.json() == []


def test_get_run_returns_404_for_unknown(client: TestClient) -> None:
    resp = client.get("/api/runs/nope")
    assert resp.status_code == 404


def test_get_run_returns_run_and_recommendations(
    client: TestClient, session_factory: sessionmaker[Session],
) -> None:
    with session_factory() as s:
        repo = Repository(s)
        repo.create_run(
            run_id="r_demo",
            window_label="opening",
            trading_day=dt.date(2026, 5, 30),
            config_hash="cfg",
            started_at=dt.datetime(2026, 5, 30, 14, tzinfo=dt.UTC),
        )
        s.commit()
    resp = client.get("/api/runs/r_demo")
    assert resp.status_code == 200
    body = resp.json()
    assert body["run"]["run_id"] == "r_demo"
    assert body["recommendations"] == []


def test_leaderboard_filter_by_window(
    client: TestClient, session_factory: sessionmaker[Session],
) -> None:
    with session_factory() as s:
        repo = Repository(s)
        track = repo.list_strategy_tracks()[0]
        repo.upsert_leaderboard_entry(
            leaderboard_id="lb_demo",
            window_start=dt.date(2026, 5, 1),
            window_end=dt.date(2026, 5, 30),
            track_id=track.track_id,
            cumulative_return_pct=Decimal("0.08"),
            rank=1,
        )
        s.commit()
    resp = client.get(
        "/api/leaderboard?window_start=2026-05-01&window_end=2026-05-30",
    )
    assert resp.status_code == 200
    rows = resp.json()
    assert len(rows) == 1
    assert rows[0]["rank"] == 1
    assert rows[0]["cumulative_return_pct"] == "0.080000"


def test_agent_scores_filters_by_track(
    client: TestClient, session_factory: sessionmaker[Session],
) -> None:
    with session_factory() as s:
        repo = Repository(s)
        track = repo.list_strategy_tracks()[0]
        repo.upsert_agent_score(
            score_id="s_demo",
            agent_id="momentum",
            track_id=track.track_id,
            as_of_date=dt.date(2026, 5, 30),
            reward_score=Decimal("0.02"),
            observation_count=1,
        )
        s.commit()
    resp = client.get(f"/api/agent-scores?track_id={track.track_id}")
    assert resp.status_code == 200
    rows = resp.json()
    assert len(rows) == 1
    assert rows[0]["agent_id"] == "momentum"
    assert rows[0]["reward_score"] == "0.020000"


def test_judge_regret_returns_seeded_rows(
    client: TestClient, session_factory: sessionmaker[Session],
) -> None:
    with session_factory() as s:
        repo = Repository(s)
        track = repo.list_strategy_tracks()[0]
        repo.upsert_judge_regret(
            regret_id="r_demo",
            judge_agent_id="judge_calmar",
            track_id=track.track_id,
            as_of_date=dt.date(2026, 5, 30),
            selected_reward=Decimal("0.02"),
            best_alternative_reward=Decimal("0.05"),
            regret=Decimal("0.03"),
            observation_count=2,
        )
        s.commit()
    resp = client.get("/api/judge-regret")
    assert resp.status_code == 200
    rows = resp.json()
    assert len(rows) == 1
    assert rows[0]["regret"] == "0.030000"


def test_invalid_date_returns_400(client: TestClient) -> None:
    resp = client.get("/api/leaderboard?window_start=not-a-date")
    assert resp.status_code == 400


def test_websocket_endpoint_accepts_connections(
    client: TestClient,
) -> None:
    with client.websocket_connect("/ws/events") as ws:
        # Closing immediately should not raise; the bus has no subscribers
        # publishing yet so receive would block. We just exercise the
        # handshake here.
        ws.close()


def test_post_events_accepts_and_validates(client: TestClient) -> None:
    payload = {
        "event": "window_started",
        "run_id": "r_demo",
        "track_id": "aggressive",
        "agent_id": None,
        "symbol": "AAPL",
        "payload": {"window_label": "opening"},
    }
    resp = client.post("/api/events", json=payload)
    assert resp.status_code == 202
    body = resp.json()
    assert body["accepted"] is True
    assert body["event"] == "window_started"


def test_post_events_rejects_unknown_event(client: TestClient) -> None:
    resp = client.post(
        "/api/events", json={"event": "not_a_real_event", "payload": {}}
    )
    assert resp.status_code == 422


def test_post_events_rejects_malformed_json(client: TestClient) -> None:
    resp = client.post(
        "/api/events",
        content=b"{not json",
        headers={"content-type": "application/json"},
    )
    assert resp.status_code == 400
