"""Tests for the dashboard event bus + window_runner event emission."""

from __future__ import annotations

import asyncio
import datetime as dt

import pytest

from stockripper.dashboard.events import (
    DashboardEvent,
    EventBus,
    EventName,
    NullEventEmitter,
)


@pytest.fixture
def event_bus() -> EventBus:
    return EventBus(queue_size=16)


async def test_subscribe_receives_published_events(event_bus: EventBus) -> None:
    received: list[DashboardEvent] = []

    async def consumer() -> None:
        async for evt in event_bus.subscribe():
            received.append(evt)
            if len(received) >= 2:
                return

    consumer_task = asyncio.create_task(consumer())
    # Yield once so the consumer can register its queue before we publish.
    await asyncio.sleep(0)
    await event_bus.publish(
        DashboardEvent(event=EventName.WINDOW_STARTED, run_id="r1"),
    )
    await event_bus.publish(
        DashboardEvent(event=EventName.TRACK_STARTED, run_id="r1", track_id="t1"),
    )
    await asyncio.wait_for(consumer_task, timeout=2.0)

    assert len(received) == 2
    assert received[0].event == EventName.WINDOW_STARTED
    assert received[1].track_id == "t1"


async def test_multiple_subscribers_each_receive(event_bus: EventBus) -> None:
    received_a: list[DashboardEvent] = []
    received_b: list[DashboardEvent] = []

    async def consume(out: list[DashboardEvent]) -> None:
        async for evt in event_bus.subscribe():
            out.append(evt)
            return

    task_a = asyncio.create_task(consume(received_a))
    task_b = asyncio.create_task(consume(received_b))
    await asyncio.sleep(0)
    await event_bus.publish(
        DashboardEvent(event=EventName.WINDOW_STARTED, run_id="r1"),
    )
    await asyncio.wait_for(asyncio.gather(task_a, task_b), timeout=2.0)
    assert len(received_a) == 1
    assert len(received_b) == 1
    assert received_a[0].event == EventName.WINDOW_STARTED
    assert received_b[0].event == EventName.WINDOW_STARTED


async def test_publish_with_no_subscribers_does_not_raise() -> None:
    bus = EventBus()
    await bus.publish(
        DashboardEvent(event=EventName.WINDOW_STARTED, run_id="r"),
    )


async def test_null_emitter_publish_is_noop() -> None:
    emitter = NullEventEmitter()
    await emitter.publish(
        DashboardEvent(event=EventName.WINDOW_STARTED, run_id="r"),
    )


def test_dashboard_event_is_frozen() -> None:
    evt = DashboardEvent(event=EventName.TRACK_STARTED, run_id="r1")
    with pytest.raises(Exception):  # noqa: B017 — pydantic raises ValidationError
        evt.run_id = "r2"


def test_dashboard_event_default_emitted_at_is_utc() -> None:
    evt = DashboardEvent(event=EventName.WINDOW_STARTED)
    assert evt.emitted_at.tzinfo is not None
    assert evt.emitted_at.tzinfo.utcoffset(evt.emitted_at) == dt.timedelta(0)
