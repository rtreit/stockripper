"""Execution-side primitives: deterministic client_order_id.

Phase 1 only ships the ID generator; the full execution adapter (the *only*
code path allowed to call Alpaca order endpoints, per §6.3) lands in Phase 5.
"""

from stockripper.execution.client_order_id import (
    ALPACA_CLIENT_ORDER_ID_MAX,
    OrderIntent,
    build_client_order_id,
    build_intent_hash,
)

__all__ = (
    "ALPACA_CLIENT_ORDER_ID_MAX",
    "OrderIntent",
    "build_client_order_id",
    "build_intent_hash",
)
