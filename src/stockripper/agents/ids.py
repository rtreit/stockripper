"""Deterministic ID helpers for Phase-4 multi-track runs.

Replay determinism requires every persisted row to have an ID derived
from its stable inputs, not a random UUID. The helpers in this module
all produce stable 24-hex-character suffixes from a sorted, NUL-joined
list of components so two runs with identical inputs produce identical
IDs.

ID format is ``<prefix>_<sha256(...)[:24]>`` where prefix is one of:

* ``win`` — window-level run (one per call to ``run_window``).
* ``trk`` — one track's execution within a window.
* ``agr`` — one agent invocation within a track-run.
* ``rec`` — one synthesized council recommendation.
* ``dec`` — one judge decision.
* ``act`` — one action item inside a judge decision.
* ``pkt`` — one evidence packet.

Use the helpers below — never hand-roll the formatting.
"""

from __future__ import annotations

import datetime as dt
import hashlib
from typing import Final

_ID_HEX_LEN: Final[int] = 24


def _stable_digest(*parts: object) -> str:
    body = "\x00".join("" if p is None else str(p) for p in parts)
    return hashlib.sha256(body.encode("utf-8")).hexdigest()[:_ID_HEX_LEN]


def window_run_id(
    *,
    window_label: str,
    trading_day: dt.date,
    config_hash: str,
    started_at: dt.datetime,
) -> str:
    """Deterministic window-level run id.

    ``started_at`` is included so re-running the same window twice in
    one day still produces distinct ids. For replay-from-fixture you
    must reuse the original ``started_at``.
    """

    return "win_" + _stable_digest(
        window_label, trading_day.isoformat(), config_hash, started_at.isoformat()
    )


def track_run_id(*, window_run_id: str, track_id: str, packet_id: str) -> str:
    """Deterministic per-(window, track, packet) execution id."""

    return "trk_" + _stable_digest(window_run_id, track_id, packet_id)


def agent_run_id(
    *,
    track_run_id: str,
    agent_id: str,
    input_hash: str,
) -> str:
    """Deterministic per-agent-invocation id.

    Two calls to the same agent on the same input (same input_hash)
    inside the same track-run will collide — that is intentional: the
    persistence layer treats them as upserts.
    """

    return "agr_" + _stable_digest(track_run_id, agent_id, input_hash)


def recommendation_id(*, agent_run_id: str, symbol: str) -> str:
    return "rec_" + _stable_digest(agent_run_id, symbol)


def decision_id(*, track_run_id: str, judge_agent_id: str) -> str:
    return "dec_" + _stable_digest(track_run_id, judge_agent_id)


def action_id(*, decision_id: str, ordinal: int, symbol: str) -> str:
    return "act_" + _stable_digest(decision_id, ordinal, symbol)


def packet_id(*, track_id: str, window_run_id: str, symbol: str) -> str:
    return "pkt_" + _stable_digest(track_id, window_run_id, symbol)


__all__ = (
    "action_id",
    "agent_run_id",
    "decision_id",
    "packet_id",
    "recommendation_id",
    "track_run_id",
    "window_run_id",
)
