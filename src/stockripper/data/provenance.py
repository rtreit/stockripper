"""Shared provenance record stamped on every external datum.

Phase 2 keeps this in adapter return values; Phase 3 will mechanically
persist it into a ``data_provenance`` table without the adapters changing.
"""

from __future__ import annotations

import datetime as dt
import hashlib
from typing import Any

from pydantic import BaseModel, ConfigDict, Field


def _utcnow() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


class Provenance(BaseModel):
    """Where this datum came from and how to prove it.

    ``content_hash`` is sha256 over the raw provider payload (after parse
    but before adapter normalisation) so two ingests of bit-identical
    content collapse to the same hash even if metadata differs.
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    provider: str = Field(..., description="Short provider id, e.g. 'sec_edgar', 'alpaca_data'.")
    source_url: str = Field(..., description="Canonical URL the data was retrieved from.")
    retrieved_at: dt.datetime = Field(default_factory=_utcnow)
    content_hash: str = Field(..., min_length=64, max_length=64)
    request_key: str | None = Field(
        default=None,
        description="Stable cache key for the request (provider + endpoint + params).",
    )
    data_quality_warnings: tuple[str, ...] = Field(
        default=(),
        description="Adapter-emitted warnings such as 'shares_out_stale' or 'partial_facts'.",
    )

    @classmethod
    def for_payload(
        cls,
        *,
        provider: str,
        source_url: str,
        payload: Any,
        request_key: str | None = None,
        retrieved_at: dt.datetime | None = None,
        data_quality_warnings: tuple[str, ...] = (),
    ) -> Provenance:
        """Build a Provenance with ``content_hash`` derived from ``payload``."""

        hasher = hashlib.sha256()
        if isinstance(payload, (bytes, bytearray)):
            hasher.update(bytes(payload))
        elif isinstance(payload, str):
            hasher.update(payload.encode("utf-8"))
        else:
            import json

            hasher.update(
                json.dumps(payload, sort_keys=True, default=str, separators=(",", ":")).encode("utf-8")
            )
        return cls(
            provider=provider,
            source_url=source_url,
            content_hash=hasher.hexdigest(),
            request_key=request_key,
            retrieved_at=retrieved_at if retrieved_at is not None else _utcnow(),
            data_quality_warnings=data_quality_warnings,
        )


__all__ = ("Provenance",)
