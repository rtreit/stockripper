"""Small helpers shared across tool implementations."""

from __future__ import annotations

from datetime import date, datetime
from typing import Any

from dateutil import parser as date_parser


def parse_dt(value: str | datetime | None) -> datetime | None:
    """Parse an ISO-8601 string (or pass-through datetime) into a datetime."""

    if value is None or isinstance(value, datetime):
        return value
    return date_parser.isoparse(value)


def parse_date(value: str | date | None) -> date | None:
    """Parse an ISO-8601 date string into a :class:`date`."""

    if value is None or isinstance(value, date):
        return value
    return date_parser.isoparse(value).date()


def to_jsonable(obj: Any) -> Any:
    """Recursively convert alpaca-py / pydantic / dataclass results to JSON-native types.

    The MCP wire format expects plain dicts/lists/scalars; alpaca-py returns
    Pydantic models, enums, datetimes, and UUIDs that need explicit coercion.
    """

    if obj is None or isinstance(obj, (str, int, float, bool)):
        return obj
    if isinstance(obj, (datetime, date)):
        return obj.isoformat()
    if hasattr(obj, "model_dump"):
        return to_jsonable(obj.model_dump(mode="json"))
    if hasattr(obj, "_asdict"):
        return to_jsonable(obj._asdict())
    if isinstance(obj, dict):
        return {str(k): to_jsonable(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple, set, frozenset)):
        return [to_jsonable(v) for v in obj]
    return str(obj)


__all__ = ("parse_date", "parse_dt", "to_jsonable")
