"""Typed reason codes attached to each universe candidate.

Reason codes are enum values, not free-form strings, so the dashboard and
analytics layer can filter, count, and chart them without parsing prose.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import StrEnum
from typing import Any


class CandidateReasonCode(StrEnum):
    """Why a symbol was admitted (or kept) in a track's candidate set."""

    PASSES_ADV_FLOOR = "passes_adv_floor"
    PASSES_PRICE_FLOOR = "passes_price_floor"
    IN_MARKET_CAP_BAND = "in_market_cap_band"
    INSTRUMENT_ALLOWED = "instrument_allowed"
    HAS_RECENT_8K = "has_recent_8k"
    LOW_VISIBILITY = "low_visibility"
    HIDDEN_GEM_BUCKET = "hidden_gem_bucket"
    BENCHMARK_HOLDING = "benchmark_holding"
    RANDOM_BASELINE_PICK = "random_baseline_pick"
    QUANT_SIGNAL_HIT = "quant_signal_hit"


@dataclass(frozen=True)
class CandidateReason:
    """A single typed reason with structured parameters.

    ``params`` is intentionally a free-form dict so each code can carry the
    measurement that satisfied it (e.g. ``{"adv_usd": 12_300_000.0,
    "floor_usd": 5_000_000.0}``) without inflating the enum.
    """

    code: CandidateReasonCode
    params: dict[str, Any] = field(default_factory=dict)

    def render(self) -> str:
        """Compact human-readable form for Rich tables / CLI output."""

        if not self.params:
            return self.code.value
        bits = ",".join(f"{k}={v}" for k, v in self.params.items())
        return f"{self.code.value}({bits})"


__all__ = ("CandidateReason", "CandidateReasonCode")
