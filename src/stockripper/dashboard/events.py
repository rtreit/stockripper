"""Async pub-sub event bus for dashboard live feed (spec §19.2)."""

from __future__ import annotations

import asyncio
import contextlib
import datetime as dt
import logging
from collections.abc import AsyncIterator
from enum import StrEnum
from typing import Any, Final, Protocol

import httpx
from pydantic import BaseModel, ConfigDict, Field

_LOG = logging.getLogger(__name__)


class EventName(StrEnum):
    WINDOW_STARTED = "window_started"
    TRACK_STARTED = "track_started"
    AGENT_COMPLETED = "agent_completed"
    RECOMMENDATION_EMITTED = "recommendation_emitted"
    JUDGE_DECIDED = "judge_decided"
    ACTION_SUBMITTED = "action_submitted"
    TRACK_COMPLETED = "track_completed"
    WINDOW_COMPLETED = "window_completed"
    KILL_SWITCH_CHANGED = "kill_switch_changed"
    TRACK_PAUSE_CHANGED = "track_pause_changed"


class DashboardEvent(BaseModel):
    """One structured event emitted into the live dashboard feed."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    event: EventName
    run_id: str | None = None
    track_id: str | None = None
    agent_id: str | None = None
    symbol: str | None = None
    payload: dict[str, Any] = Field(default_factory=dict)
    emitted_at: dt.datetime = Field(default_factory=lambda: dt.datetime.now(dt.UTC))


class EventEmitter(Protocol):
    """Anything that can accept dashboard events (real bus or no-op)."""

    async def publish(self, event: DashboardEvent) -> None: ...


class NullEventEmitter:
    """No-op emitter used when the dashboard is not attached."""

    async def publish(self, event: DashboardEvent) -> None:
        return None


class HttpEventEmitter:
    """Push events to a running dashboard server's POST /api/events endpoint.

    Lets a separate process (e.g. ``stockripper agents run-window``) feed
    the live Council Feed of a dashboard hosted elsewhere. Failures are
    swallowed and logged so the orchestrator hot path is never broken.
    """

    def __init__(
        self,
        dashboard_url: str,
        *,
        timeout_s: float = 1.5,
        client: httpx.AsyncClient | None = None,
    ) -> None:
        base = dashboard_url.rstrip("/")
        self._endpoint = f"{base}/api/events"
        self._timeout_s = timeout_s
        self._own_client = client is None
        self._client = client or httpx.AsyncClient(timeout=timeout_s)

    async def publish(self, event: DashboardEvent) -> None:
        try:
            await self._client.post(
                self._endpoint,
                content=event.model_dump_json(),
                headers={"content-type": "application/json"},
                timeout=self._timeout_s,
            )
        except Exception as exc:  # pragma: no cover - best-effort transport
            _LOG.debug("dashboard event push failed: %s", exc)

    async def aclose(self) -> None:
        if self._own_client:
            await self._client.aclose()


_DEFAULT_QUEUE_SIZE: Final[int] = 1024


class EventBus:
    """Fan-out async event bus.

    ``publish`` is non-blocking (drops the oldest item on a full queue
    to keep the orchestrator path latency-bounded); ``subscribe``
    returns an :class:`AsyncIterator` that yields events until the bus
    is closed.
    """

    def __init__(self, *, queue_size: int = _DEFAULT_QUEUE_SIZE) -> None:
        self._queue_size = queue_size
        self._subscribers: list[asyncio.Queue[DashboardEvent]] = []
        self._lock = asyncio.Lock()
        self._closed = False

    async def publish(self, event: DashboardEvent) -> None:
        if self._closed:
            return
        async with self._lock:
            subscribers = list(self._subscribers)
        for queue in subscribers:
            if queue.full():
                with contextlib.suppress(asyncio.QueueEmpty):
                    queue.get_nowait()
            try:
                queue.put_nowait(event)
            except asyncio.QueueFull:  # pragma: no cover — defensive
                continue

    async def subscribe(self) -> AsyncIterator[DashboardEvent]:
        queue: asyncio.Queue[DashboardEvent] = asyncio.Queue(self._queue_size)
        async with self._lock:
            self._subscribers.append(queue)
        try:
            while True:
                event = await queue.get()
                if event.event == EventName.WINDOW_COMPLETED and self._closed:
                    yield event
                    return
                yield event
        finally:
            async with self._lock:
                if queue in self._subscribers:
                    self._subscribers.remove(queue)

    async def close(self) -> None:
        self._closed = True
        async with self._lock:
            self._subscribers.clear()

    @property
    def subscriber_count(self) -> int:
        return len(self._subscribers)


__all__ = (
    "DashboardEvent",
    "EventBus",
    "EventEmitter",
    "EventName",
    "HttpEventEmitter",
    "NullEventEmitter",
)
