"""StockRipper dashboard MVP (spec §19 / §25 Phase 6).

Provides:

* :class:`stockripper.dashboard.events.EventBus` — async pub-sub used
  by the orchestrator to publish per-window structured events.
* :func:`stockripper.dashboard.app.build_app` — FastAPI app factory
  with REST endpoints for runs, tracks, leaderboard, agent scores,
  judge regret, and a WebSocket ``/ws/events`` stream.
* ``stockripper.dashboard.static`` — minimal vanilla-JS SPA served at
  ``/``.

The dashboard is read-only against the ledger; the orchestrator
optionally publishes events into it via :class:`EventBus`.
"""

from __future__ import annotations

from stockripper.dashboard.app import build_app
from stockripper.dashboard.events import (
    DashboardEvent,
    EventBus,
    EventName,
    NullEventEmitter,
)

__all__ = (
    "DashboardEvent",
    "EventBus",
    "EventName",
    "NullEventEmitter",
    "build_app",
)
