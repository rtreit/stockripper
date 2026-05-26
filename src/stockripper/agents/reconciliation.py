"""Reconciliation worker: pull Alpaca truth via MCP, write to local ledger.

Per PROJECT_SPEC.md §6.3 and §12 the Alpaca client surface for the
StockRipper runtime is the alpaca-mcp server — not a hand-rolled HTTP client.
This module is the Phase-1 read-path consumer: it spawns the MCP client,
calls ``get_account`` / ``get_orders`` / ``get_positions`` /
``get_portfolio_history``, parses the responses, and writes a
``track_snapshot`` row per track plus an upsert into ``orders``/``fills``.

It is intentionally conservative for now: there is exactly one paper Alpaca
account, and per-spec the system runs *all* tracks as sub-accounts of that
single paper account. So at Phase 1 every track snapshot gets the same
account totals — the per-track decomposition lands in later phases when we
have actual per-track holdings.
"""

from __future__ import annotations

import datetime as dt
import json
import logging
import uuid
from dataclasses import dataclass, field
from decimal import Decimal
from typing import Any

from mcp.types import CallToolResult, TextContent
from sqlalchemy.orm import Session

from stockripper.agents import AlpacaMcpClient
from stockripper.config import StockripperSettings, load_settings
from stockripper.db.repository import Repository

logger = logging.getLogger(__name__)


@dataclass
class ReconciliationReport:
    """Per-call summary used by tests and the CLI to verify success."""

    captured_at: dt.datetime
    account_equity: Decimal
    account_cash: Decimal
    buying_power: Decimal | None
    orders_seen: int = 0
    fills_seen: int = 0
    snapshots_written: int = 0
    per_track_snapshots: dict[str, str] = field(default_factory=dict)


# ----------------------------------------------------------------------
# MCP payload parsing
# ----------------------------------------------------------------------
def _payload_from_tool_result(result: CallToolResult) -> Any:
    """Extract the JSON-ish payload from an MCP ``CallToolResult``.

    The alpaca-mcp server serialises responses with ``to_jsonable`` and
    surfaces them via ``structuredContent`` when available. Older clients see
    a single ``TextContent`` block containing the JSON text. We accept both.
    """

    if result.isError:
        raise RuntimeError(
            f"MCP tool returned an error: {_first_text(result.content)!r}"
        )
    if result.structuredContent is not None:
        return result.structuredContent
    text = _first_text(result.content)
    if text is None:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


def _first_text(blocks: list[Any]) -> str | None:
    for block in blocks:
        if isinstance(block, TextContent):
            return block.text
    return None


def _unwrap(payload: Any) -> Any:
    """Some MCP results wrap the real payload in ``{'result': ...}``."""

    if isinstance(payload, dict) and set(payload.keys()) == {"result"}:
        return payload["result"]
    return payload


def _to_decimal(value: Any) -> Decimal | None:
    if value is None or value == "":
        return None
    if isinstance(value, Decimal):
        return value
    return Decimal(str(value))


# ----------------------------------------------------------------------
# Reconciliation entry points
# ----------------------------------------------------------------------
async def reconcile_via_mcp(
    session: Session,
    *,
    settings: StockripperSettings | None = None,
    captured_at: dt.datetime | None = None,
    run_id: str | None = None,
) -> ReconciliationReport:
    """Run a full reconciliation pass and write results into ``session``.

    The caller owns the transaction boundary. On success the session is left
    populated with new rows but uncommitted; commit via
    :func:`stockripper.db.engine.session_scope` or by hand.
    """

    cfg = settings if settings is not None else load_settings()
    cfg.assert_paper_only()  # universal floor #1 — fail-closed before any I/O

    async with AlpacaMcpClient.spawn(
        api_key_id=cfg.alpaca_api_key_id.get_secret_value(),
        api_secret_key=cfg.alpaca_api_secret_key.get_secret_value(),
    ) as client:
        account = _unwrap(_payload_from_tool_result(
            await client.call_tool("get_account", {}),
        ))
        orders = _unwrap(_payload_from_tool_result(
            await client.call_tool("get_orders", {"status": "all", "limit": 200}),
        ))

    return apply_reconciliation(
        session,
        account_payload=account,
        orders_payload=orders or [],
        captured_at=captured_at,
        run_id=run_id,
    )


def apply_reconciliation(
    session: Session,
    *,
    account_payload: dict[str, Any],
    orders_payload: list[dict[str, Any]],
    captured_at: dt.datetime | None = None,
    run_id: str | None = None,
) -> ReconciliationReport:
    """Synchronous, MCP-free core: write parsed Alpaca payloads to the ledger.

    Separated from :func:`reconcile_via_mcp` so unit tests can drive the
    write path with canned payloads and no subprocess.
    """

    repo = Repository(session)
    now = captured_at if captured_at is not None else dt.datetime.now(dt.UTC)

    equity = _to_decimal(account_payload.get("equity")) or Decimal("0")
    cash = _to_decimal(account_payload.get("cash")) or Decimal("0")
    buying_power = _to_decimal(account_payload.get("buying_power"))

    report = ReconciliationReport(
        captured_at=now,
        account_equity=equity,
        account_cash=cash,
        buying_power=buying_power,
    )

    # ------------------------------------------------------------------
    # Orders + fills: upsert one row per Alpaca order. Track attribution
    # comes from the alpaca-side ``client_order_id`` prefix (the
    # deterministic generator embeds the track name) or, for legacy /
    # external orders, defaults to ``unattributed`` so we don't lose them.
    # ------------------------------------------------------------------
    for raw in orders_payload:
        coid = str(raw.get("client_order_id") or "")
        track_id = _track_from_client_order_id(coid)
        repo.upsert_order_from_alpaca(track_id=track_id, alpaca_order=raw)
        report.orders_seen += 1

        filled_qty = _to_decimal(raw.get("filled_qty"))
        filled_at_raw = raw.get("filled_at")
        if filled_qty and filled_qty > 0 and filled_at_raw:
            filled_at = _parse_dt(filled_at_raw) or now
            avg_price = _to_decimal(raw.get("filled_avg_price")) or Decimal("0")
            fill_id = f"fill_{coid or raw.get('id', uuid.uuid4().hex)}"
            repo.record_fill(
                fill_id=fill_id,
                local_order_id=coid or str(raw["id"]),
                filled_qty=filled_qty,
                filled_avg_price=avg_price,
                filled_at=filled_at,
            )
            report.fills_seen += 1

    # ------------------------------------------------------------------
    # Per-track snapshots. Phase 1 records the same account-level totals
    # against every enabled track; later phases populate per-track equity
    # from the strategy-tracks sub-accounting.
    # ------------------------------------------------------------------
    for track in repo.list_strategy_tracks(enabled_only=True):
        snapshot_id = f"snap_{track.track_id}_{int(now.timestamp())}"
        repo.record_track_snapshot(
            snapshot_id=snapshot_id,
            run_id=run_id,
            track_id=track.track_id,
            captured_at=now,
            equity=equity,
            cash=cash,
            buying_power=buying_power,
        )
        report.snapshots_written += 1
        report.per_track_snapshots[track.track_id] = snapshot_id

    return report


def _track_from_client_order_id(client_order_id: str) -> str:
    """Recover the track id from a deterministic ``client_order_id``.

    The Phase-1 generator emits ``<4-char track prefix>_<digest>``. We try a
    longest-match against the seeded track ids and fall back to
    ``unattributed`` so reconciliation never loses an order.
    """

    if "_" not in client_order_id:
        return "unattributed"
    prefix = client_order_id.split("_", 1)[0].lower()
    # Defer the import to avoid circular dependency at module import time.
    from stockripper.tracks import DEFAULT_TRACKS

    for spec in DEFAULT_TRACKS:
        if spec.track_id.lower().startswith(prefix):
            return spec.track_id
    return "unattributed"


def _parse_dt(value: Any) -> dt.datetime | None:
    if value is None:
        return None
    if isinstance(value, dt.datetime):
        return value if value.tzinfo else value.replace(tzinfo=dt.UTC)
    text = str(value).replace("Z", "+00:00")
    try:
        parsed = dt.datetime.fromisoformat(text)
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=dt.UTC)


__all__ = (
    "ReconciliationReport",
    "apply_reconciliation",
    "reconcile_via_mcp",
)
