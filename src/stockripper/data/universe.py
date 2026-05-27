"""Universe builder.

Given a per-track :class:`UniversePolicyParams`, produces a deterministic
ordered list of :class:`Candidate` symbols with structured
:class:`CandidateReason` records describing exactly why each symbol was
admitted.

The builder is intentionally **pluggable**: market-data, fundamentals, and
news access happen through small adapter callables so unit tests can drive
the entire flow with deterministic in-memory fakes (no network).
"""

from __future__ import annotations

import datetime as dt
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass, field
from decimal import Decimal
from typing import Protocol

from stockripper.data.reasons import CandidateReason, CandidateReasonCode
from stockripper.data.universe_policy import (
    DEFAULT_UNIVERSE_POLICIES,
    InstrumentType,
    MarketCapBand,
    UniversePolicyParams,
)


# ---------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class AssetRecord:
    """Static metadata for a tradable asset."""

    symbol: str
    name: str
    exchange: str
    tradable: bool
    shortable: bool
    fractionable: bool
    is_etf: bool = False
    is_leveraged_etf: bool = False


@dataclass(frozen=True)
class AssetSnapshot:
    """Refresh-time view of an asset used by the builder."""

    symbol: str
    last_price: Decimal
    adv_usd_20d: Decimal
    market_cap_usd: Decimal | None
    recent_8k_within_days: int | None  # ``None`` if unknown / not searched
    recent_news_count_30d: int | None  # ``None`` if not measured


@dataclass(frozen=True)
class UniverseBuildRequest:
    """Deterministic input envelope for a universe build."""

    track_id: str
    as_of: dt.date
    window_id: str
    limit: int = 200


@dataclass(frozen=True)
class Candidate:
    symbol: str
    reasons: tuple[CandidateReason, ...]
    bucket: str  # "core" | "hidden_gem" | "benchmark"
    snapshot: AssetSnapshot


@dataclass(frozen=True)
class UniverseBuildResult:
    request: UniverseBuildRequest
    candidates: tuple[Candidate, ...]
    rejected_count: int
    diagnostics: dict[str, int] = field(default_factory=dict)


# ---------------------------------------------------------------------------
# Adapter contracts
# ---------------------------------------------------------------------------
class SnapshotProvider(Protocol):
    """Returns refresh-time snapshot info for a given asset universe.

    Implementations may batch, cache, or stream; the universe builder only
    cares that calling with the same inputs produces the same outputs.
    """

    def get_snapshots(
        self, symbols: Iterable[str], *, as_of: dt.date,
    ) -> Mapping[str, AssetSnapshot]: ...


# ---------------------------------------------------------------------------
# Builder
# ---------------------------------------------------------------------------
@dataclass
class UniverseBuilder:
    """Pure-logic builder.

    External I/O is delegated to two callables:

    - ``assets_loader`` returns the static tradable-asset list.
    - ``snapshot_provider`` returns refresh-time per-symbol data.
    """

    assets_loader: Callable[[], Iterable[AssetRecord]]
    snapshot_provider: SnapshotProvider
    policies: Mapping[str, UniversePolicyParams] = field(
        default_factory=lambda: DEFAULT_UNIVERSE_POLICIES
    )

    def build(self, request: UniverseBuildRequest) -> UniverseBuildResult:
        policy = self.policies.get(request.track_id)
        if policy is None:
            raise KeyError(f"No universe policy registered for track {request.track_id!r}")

        assets = [a for a in self.assets_loader() if a.tradable]
        snapshots = self.snapshot_provider.get_snapshots(
            (a.symbol for a in assets), as_of=request.as_of,
        )

        diagnostics: dict[str, int] = {
            "total_assets": len(assets),
            "missing_snapshot": 0,
            "rejected_price_floor": 0,
            "rejected_adv_floor": 0,
            "rejected_cap_band": 0,
            "rejected_instrument": 0,
            "admitted_core": 0,
            "admitted_low_visibility": 0,
        }
        admitted: list[Candidate] = []

        for asset in assets:
            snap = snapshots.get(asset.symbol.upper())
            if snap is None:
                diagnostics["missing_snapshot"] += 1
                continue

            verdict = _evaluate(asset, snap, policy)
            if verdict.kind == "admit":
                bucket = verdict.bucket
                if bucket == "hidden_gem":
                    diagnostics["admitted_low_visibility"] += 1
                else:
                    diagnostics["admitted_core"] += 1
                admitted.append(
                    Candidate(
                        symbol=asset.symbol.upper(),
                        reasons=tuple(verdict.reasons),
                        bucket=bucket,
                        snapshot=snap,
                    )
                )
            else:
                diagnostics[verdict.reject_key] = diagnostics.get(verdict.reject_key, 0) + 1

        admitted.sort(key=lambda c: (-int(c.snapshot.adv_usd_20d), c.symbol))
        truncated = admitted[: request.limit]
        return UniverseBuildResult(
            request=request,
            candidates=tuple(truncated),
            rejected_count=len(assets) - len(truncated),
            diagnostics=diagnostics,
        )


# ---------------------------------------------------------------------------
# Per-asset evaluation
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class _Verdict:
    kind: str  # "admit" | "reject"
    reasons: tuple[CandidateReason, ...] = ()
    bucket: str = "core"
    reject_key: str = ""


def _evaluate(
    asset: AssetRecord, snap: AssetSnapshot, policy: UniversePolicyParams,
) -> _Verdict:
    reasons: list[CandidateReason] = []

    instrument = _classify_instrument(asset)
    if instrument not in policy.instrument_types_allowed:
        return _Verdict(kind="reject", reject_key="rejected_instrument")
    reasons.append(
        CandidateReason(
            code=CandidateReasonCode.INSTRUMENT_ALLOWED,
            params={"instrument": instrument.value},
        )
    )

    if snap.last_price < policy.price_floor_usd:
        return _Verdict(kind="reject", reject_key="rejected_price_floor")
    reasons.append(
        CandidateReason(
            code=CandidateReasonCode.PASSES_PRICE_FLOOR,
            params={"price": float(snap.last_price), "floor": float(policy.price_floor_usd)},
        )
    )

    if snap.adv_usd_20d < policy.min_adv_usd:
        return _Verdict(kind="reject", reject_key="rejected_adv_floor")
    reasons.append(
        CandidateReason(
            code=CandidateReasonCode.PASSES_ADV_FLOOR,
            params={"adv_usd": float(snap.adv_usd_20d), "floor": float(policy.min_adv_usd)},
        )
    )

    band = MarketCapBand.classify(snap.market_cap_usd)
    if band is None or band not in policy.market_cap_bands_allowed:
        return _Verdict(kind="reject", reject_key="rejected_cap_band")
    reasons.append(
        CandidateReason(
            code=CandidateReasonCode.IN_MARKET_CAP_BAND,
            params={"band": band.value},
        )
    )

    bucket = "core"
    if policy.low_visibility_enabled and _is_low_visibility(snap, policy, band):
        bucket = "hidden_gem"
        reasons.append(
            CandidateReason(
                code=CandidateReasonCode.LOW_VISIBILITY,
                params={
                    "news_30d": snap.recent_news_count_30d,
                    "max_allowed": policy.low_visibility_max_news_30d,
                },
            )
        )
        if policy.require_recent_catalyst_days is not None and snap.recent_8k_within_days is not None:
            reasons.append(
                CandidateReason(
                    code=CandidateReasonCode.HAS_RECENT_8K,
                    params={"within_days": snap.recent_8k_within_days},
                )
            )
        reasons.append(CandidateReason(code=CandidateReasonCode.HIDDEN_GEM_BUCKET))

    return _Verdict(kind="admit", reasons=tuple(reasons), bucket=bucket)


def _classify_instrument(asset: AssetRecord) -> InstrumentType:
    if asset.is_leveraged_etf:
        return InstrumentType.LEVERAGED_ETF
    if asset.is_etf:
        return InstrumentType.ETF
    return InstrumentType.EQUITY_LONG


def _is_low_visibility(
    snap: AssetSnapshot, policy: UniversePolicyParams, band: MarketCapBand,
) -> bool:
    # Restrict to small/micro/nano caps — anything bigger isn't "low visibility".
    if band not in {MarketCapBand.SMALL, MarketCapBand.MICRO, MarketCapBand.NANO}:
        return False
    if snap.recent_news_count_30d is None:
        return False
    if snap.recent_news_count_30d >= policy.low_visibility_max_news_30d:
        return False
    return not (policy.require_recent_catalyst_days is not None and (snap.recent_8k_within_days is None or snap.recent_8k_within_days > policy.require_recent_catalyst_days))


__all__ = (
    "AssetRecord",
    "AssetSnapshot",
    "Candidate",
    "SnapshotProvider",
    "UniverseBuildRequest",
    "UniverseBuildResult",
    "UniverseBuilder",
)
