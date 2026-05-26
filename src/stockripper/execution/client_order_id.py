"""Deterministic ``client_order_id`` generator.

Idempotent submission to Alpaca demands that *the same intent* always produces
*the same client_order_id*, so retries collapse to the same order rather than
duplicating it. We compose the ID from three semantic dimensions:

- ``track_id`` — which strategy track owns the intent. Including this
  prevents collisions when two tracks independently want to long the same
  symbol in the same decision window.
- ``intent_hash`` — a stable hash over the *intent payload* (symbol, side,
  qty/notional, prices, order type, time-in-force, leg structure). Two
  semantically-equivalent intents must hash identically; two different
  intents must not.
- ``window_id`` — the decision-window identifier (e.g. ``2025-05-26-open``).
  Including it prevents the same intent recurring on a later window from
  being silently coalesced with the original (which is usually a bug).

Alpaca limits ``client_order_id`` to 48 characters and to a restricted
character set. We use 36 characters built from URL-safe base64 of a SHA-256
digest plus a short readable track prefix so logs are skimmable.
"""

from __future__ import annotations

import base64
import hashlib
import json
import re
from dataclasses import dataclass
from decimal import Decimal
from typing import Any, Final

ALPACA_CLIENT_ORDER_ID_MAX: Final[int] = 48
"""Per Alpaca docs, ``client_order_id`` is capped at 48 characters."""

# Alpaca accepts ASCII letters, digits, hyphens, underscores, and dots in
# ``client_order_id``. We restrict to ``[A-Za-z0-9_-]`` so the value is also
# URL-, log-, and shell-safe.
_ID_CHARSET = re.compile(r"^[A-Za-z0-9_\-]+$")

# Reserve 4 chars for a track prefix + a separator ('xxxx_') and ensure the
# total output never exceeds 36 chars. 36 leaves comfortable headroom under
# the 48-char limit for any future prefix changes.
_TARGET_LENGTH: Final[int] = 36
_TRACK_PREFIX_LEN: Final[int] = 4


@dataclass(frozen=True)
class OrderIntent:
    """Canonical, hashable description of a pending order intent.

    All ``Decimal`` fields are normalized to strings via :func:`str` when
    serialised so that ``Decimal('10.00') == Decimal('10.0')`` produces the
    same hash (the JSON serialisation preserves trailing zeros, but
    :meth:`normalize` strips them).
    """

    symbol: str
    side: str  # buy | sell | sell_short | buy_to_cover
    order_type: str  # market | limit | stop | stop_limit | multi_leg
    time_in_force: str  # day | gtc | ioc | fok
    qty: Decimal | None = None
    notional: Decimal | None = None
    limit_price: Decimal | None = None
    stop_price: Decimal | None = None
    legs: tuple[tuple[str, str], ...] | None = None  # ((leg_symbol, leg_side), ...)

    def __post_init__(self) -> None:
        if (self.qty is None) == (self.notional is None):
            raise ValueError(
                "OrderIntent requires exactly one of qty or notional to be set."
            )

    def to_canonical(self) -> dict[str, Any]:
        """Return a stable dict view used as the basis of the intent hash."""

        return {
            "symbol": self.symbol.upper(),
            "side": self.side.lower(),
            "order_type": self.order_type.lower(),
            "time_in_force": self.time_in_force.lower(),
            "qty": _normalize_decimal(self.qty),
            "notional": _normalize_decimal(self.notional),
            "limit_price": _normalize_decimal(self.limit_price),
            "stop_price": _normalize_decimal(self.stop_price),
            "legs": (
                [[s.upper(), d.lower()] for (s, d) in self.legs]
                if self.legs is not None
                else None
            ),
        }


def _normalize_decimal(value: Decimal | None) -> str | None:
    """Render a Decimal in a hash-stable way (no scientific notation, no exponent)."""

    if value is None:
        return None
    normalized = value.normalize()
    # normalize() can produce '1E+2' for e.g. Decimal('100'); coerce to plain.
    if normalized == 0:
        return "0"
    sign, digits, exponent = normalized.as_tuple()
    if isinstance(exponent, str):
        # Special values like 'F' (NaN) — round-trip via str.
        return str(value)
    if exponent >= 0:
        text = "".join(map(str, digits)) + ("0" * exponent)
    else:
        digits_str = "".join(map(str, digits))
        if -exponent >= len(digits_str):
            text = "0." + digits_str.rjust(-exponent, "0")
        else:
            split_at = len(digits_str) + exponent
            text = digits_str[:split_at] + "." + digits_str[split_at:]
    return ("-" if sign else "") + text


def build_intent_hash(intent: OrderIntent) -> str:
    """SHA-256 the canonical JSON of ``intent`` and return the hex digest."""

    payload = json.dumps(intent.to_canonical(), sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _short_prefix(track_id: str) -> str:
    """Return a 4-char readable prefix derived from ``track_id``.

    We take the first 4 alphanumerics of the track_id (lowercased), padded
    with ``z`` so very-short identifiers still emit a consistent 4-char
    leader. ``z`` is rare enough in real track names to be obviously
    synthetic in logs.
    """

    cleaned = re.sub(r"[^A-Za-z0-9]", "", track_id).lower()
    if not cleaned:
        cleaned = "trck"
    return (cleaned + ("z" * _TRACK_PREFIX_LEN))[:_TRACK_PREFIX_LEN]


def build_client_order_id(
    *,
    track_id: str,
    intent_hash: str,
    window_id: str,
) -> str:
    """Compose the final ``client_order_id``.

    The structure is::

        <4-char track prefix>_<urlsafe-base64-of-sha256(track|intent|window)>

    Always 36 characters, always matches ``[A-Za-z0-9_-]+``, fully determined
    by the inputs (no clock or random component).
    """

    if not track_id:
        raise ValueError("track_id must be a non-empty string.")
    if not intent_hash:
        raise ValueError("intent_hash must be a non-empty string.")
    if not window_id:
        raise ValueError("window_id must be a non-empty string.")

    prefix = _short_prefix(track_id)
    raw = "|".join((track_id, intent_hash, window_id)).encode("utf-8")
    digest = hashlib.sha256(raw).digest()
    # urlsafe_b64encode emits ``[A-Za-z0-9_-=]`` — strip ``=`` padding.
    encoded = base64.urlsafe_b64encode(digest).decode("ascii").rstrip("=")

    body_budget = _TARGET_LENGTH - _TRACK_PREFIX_LEN - 1  # one char for '_'
    client_order_id = f"{prefix}_{encoded[:body_budget]}"

    assert len(client_order_id) == _TARGET_LENGTH, (
        f"client_order_id length {len(client_order_id)} != target {_TARGET_LENGTH}"
    )
    assert _ID_CHARSET.match(client_order_id), (
        f"client_order_id {client_order_id!r} contains forbidden characters"
    )
    assert len(client_order_id) <= ALPACA_CLIENT_ORDER_ID_MAX, (
        "client_order_id exceeds Alpaca's 48-char limit"
    )
    return client_order_id
