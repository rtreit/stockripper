"""Tests for deterministic ID helpers (Phase 4 replay determinism)."""

from __future__ import annotations

import datetime as dt

from stockripper.agents.ids import (
    action_id,
    agent_run_id,
    decision_id,
    packet_id,
    recommendation_id,
    track_run_id,
    window_run_id,
)


def test_window_run_id_is_deterministic_for_identical_inputs() -> None:
    when = dt.datetime(2026, 5, 28, 14, 30, 0, tzinfo=dt.UTC)
    a = window_run_id(
        window_label="opening",
        trading_day=when.date(),
        config_hash="cfg",
        started_at=when,
    )
    b = window_run_id(
        window_label="opening",
        trading_day=when.date(),
        config_hash="cfg",
        started_at=when,
    )
    assert a == b
    assert a.startswith("win_")
    assert len(a) == len("win_") + 24


def test_window_run_id_differs_when_any_part_differs() -> None:
    when = dt.datetime(2026, 5, 28, 14, 30, 0, tzinfo=dt.UTC)
    base = window_run_id(
        window_label="opening",
        trading_day=when.date(),
        config_hash="cfg",
        started_at=when,
    )
    diff_label = window_run_id(
        window_label="midday",
        trading_day=when.date(),
        config_hash="cfg",
        started_at=when,
    )
    diff_day = window_run_id(
        window_label="opening",
        trading_day=when.date() + dt.timedelta(days=1),
        config_hash="cfg",
        started_at=when,
    )
    diff_started = window_run_id(
        window_label="opening",
        trading_day=when.date(),
        config_hash="cfg",
        started_at=when + dt.timedelta(seconds=1),
    )
    diff_cfg = window_run_id(
        window_label="opening",
        trading_day=when.date(),
        config_hash="cfg2",
        started_at=when,
    )
    assert len({base, diff_label, diff_day, diff_started, diff_cfg}) == 5


def test_track_packet_agent_rec_decision_action_ids_all_stable() -> None:
    win = "win_" + "0" * 24
    trk = track_run_id(window_run_id=win, track_id="balanced", packet_id="pkt_1")
    pkt = packet_id(track_id="balanced", window_run_id=win, symbol="AAPL")
    agr = agent_run_id(track_run_id=trk, agent_id="value", input_hash="0" * 64)
    rec = recommendation_id(agent_run_id=agr, symbol="AAPL")
    dec = decision_id(track_run_id=trk, judge_agent_id="judge_balanced")
    act = action_id(decision_id=dec, ordinal=0, symbol="AAPL")

    # All deterministic — re-deriving with same inputs must match.
    assert trk == track_run_id(
        window_run_id=win, track_id="balanced", packet_id="pkt_1",
    )
    assert pkt == packet_id(
        track_id="balanced", window_run_id=win, symbol="AAPL",
    )
    assert agr == agent_run_id(
        track_run_id=trk, agent_id="value", input_hash="0" * 64,
    )
    assert rec == recommendation_id(agent_run_id=agr, symbol="AAPL")
    assert dec == decision_id(track_run_id=trk, judge_agent_id="judge_balanced")
    assert act == action_id(decision_id=dec, ordinal=0, symbol="AAPL")

    # And all have the expected prefix + length.
    for value, prefix in (
        (trk, "trk_"), (pkt, "pkt_"), (agr, "agr_"),
        (rec, "rec_"), (dec, "dec_"), (act, "act_"),
    ):
        assert value.startswith(prefix)
        assert len(value) == len(prefix) + 24
