"""Universe-builder acceptance + filter tests.

Drives the pluggable :class:`UniverseBuilder` with a synthetic 300-asset
corpus + in-memory snapshot provider so the test is fully offline.
"""

from __future__ import annotations

import datetime as dt
import random
from collections.abc import Iterable, Mapping
from decimal import Decimal

import pytest

from stockripper.data import (
    DEFAULT_UNIVERSE_POLICIES,
    AssetRecord,
    AssetSnapshot,
    CandidateReasonCode,
    SnapshotProvider,
    UniverseBuilder,
    UniverseBuildRequest,
)


def _synthetic_assets(n: int = 300) -> tuple[AssetRecord, ...]:
    rng = random.Random(20260527)
    out: list[AssetRecord] = []
    for i in range(n):
        symbol = f"SYM{i:03d}"
        is_etf = (i % 25 == 0)
        is_lev = (i % 75 == 0)
        out.append(
            AssetRecord(
                symbol=symbol,
                name=f"Synthetic Co {i}",
                exchange=rng.choice(("NASDAQ", "NYSE", "ARCA")),
                tradable=True,
                shortable=(i % 3 == 0),
                fractionable=True,
                is_etf=is_etf,
                is_leveraged_etf=is_lev,
            )
        )
    return tuple(out)


class _InMemorySnapshotProvider(SnapshotProvider):
    def __init__(self, snapshots: Mapping[str, AssetSnapshot]) -> None:
        self._snapshots = snapshots

    def get_snapshots(
        self, symbols: Iterable[str], *, as_of: dt.date,
    ) -> Mapping[str, AssetSnapshot]:
        return self._snapshots


def _synthetic_snapshots(assets: Iterable[AssetRecord]) -> dict[str, AssetSnapshot]:
    rng = random.Random(20260527)
    out: dict[str, AssetSnapshot] = {}
    for i, asset in enumerate(assets):
        # Build a wide spread across cap bands, prices, ADV so most tracks
        # see plenty of candidates and at least one nano-cap qualifies for
        # YOLO's low-visibility bucket.
        cap_buckets = [
            500_000_000_000,    # mega
            50_000_000_000,     # large
            5_000_000_000,      # mid
            800_000_000,        # small
            150_000_000,        # micro
            25_000_000,         # nano
        ]
        cap = Decimal(str(cap_buckets[i % len(cap_buckets)]))
        price = Decimal(str(rng.uniform(0.5, 500.0))).quantize(Decimal("0.01"))
        # Volume scaled to cap so liquidity filters bind realistically.
        base_adv = float(cap) * rng.uniform(0.0005, 0.005)
        adv = Decimal(f"{base_adv:.2f}")
        news_30d = rng.randint(0, 12)
        # Every 11th nano/micro has a recent 8-K — that's our hidden-gem set.
        recent_8k = 5 if (i % 11 == 0 and cap < 300_000_000) else None
        out[asset.symbol] = AssetSnapshot(
            symbol=asset.symbol,
            last_price=price,
            adv_usd_20d=adv,
            market_cap_usd=cap,
            recent_8k_within_days=recent_8k,
            recent_news_count_30d=news_30d,
        )
    return out


@pytest.fixture
def builder() -> UniverseBuilder:
    assets = _synthetic_assets(300)
    snaps = _synthetic_snapshots(assets)
    return UniverseBuilder(
        assets_loader=lambda: assets,
        snapshot_provider=_InMemorySnapshotProvider(snaps),
    )


def _request(track: str, limit: int = 200) -> UniverseBuildRequest:
    return UniverseBuildRequest(
        track_id=track,
        as_of=dt.date(2026, 5, 27),
        window_id="2026-05-27-open",
        limit=limit,
    )


# --------------------------------------------------------------------------
# Acceptance: 50+ candidates per enabled "broad" track
# --------------------------------------------------------------------------
@pytest.mark.parametrize("track", ("aggressive", "yolo", "quant_signal"))
def test_universe_builder_admits_at_least_50_per_broad_track(
    builder: UniverseBuilder, track: str,
) -> None:
    result = builder.build(_request(track))
    assert len(result.candidates) >= 50, (
        f"{track} only got {len(result.candidates)} candidates; "
        f"diagnostics={result.diagnostics}"
    )


def test_universe_builder_admits_enough_per_conservative(
    builder: UniverseBuilder,
) -> None:
    # Conservative is intentionally restrictive (mega+large only, $25M ADV
    # floor) — we just require something non-trivial to come through.
    result = builder.build(_request("conservative"))
    assert len(result.candidates) >= 5
    for cand in result.candidates:
        # No micro/nano caps allowed.
        assert any(
            r.code == CandidateReasonCode.IN_MARKET_CAP_BAND
            and r.params.get("band") in {"mega", "large"}
            for r in cand.reasons
        )


def test_every_candidate_carries_typed_reasons(builder: UniverseBuilder) -> None:
    result = builder.build(_request("aggressive"))
    assert result.candidates
    for cand in result.candidates:
        codes = {r.code for r in cand.reasons}
        assert CandidateReasonCode.INSTRUMENT_ALLOWED in codes
        assert CandidateReasonCode.PASSES_ADV_FLOOR in codes
        assert CandidateReasonCode.PASSES_PRICE_FLOOR in codes
        assert CandidateReasonCode.IN_MARKET_CAP_BAND in codes


def test_yolo_unlocks_low_visibility_bucket(builder: UniverseBuilder) -> None:
    result = builder.build(_request("yolo"))
    hidden = [c for c in result.candidates if c.bucket == "hidden_gem"]
    assert hidden, "YOLO should surface at least one hidden-gem candidate"
    for cand in hidden:
        codes = {r.code for r in cand.reasons}
        assert CandidateReasonCode.LOW_VISIBILITY in codes
        assert CandidateReasonCode.HIDDEN_GEM_BUCKET in codes


def test_conservative_never_returns_hidden_gem(builder: UniverseBuilder) -> None:
    result = builder.build(_request("conservative"))
    assert all(c.bucket == "core" for c in result.candidates)


def test_unknown_track_raises_key_error(builder: UniverseBuilder) -> None:
    with pytest.raises(KeyError):
        builder.build(_request("not_a_real_track"))


def test_build_is_deterministic_for_same_inputs(builder: UniverseBuilder) -> None:
    a = builder.build(_request("aggressive"))
    b = builder.build(_request("aggressive"))
    assert tuple(c.symbol for c in a.candidates) == tuple(c.symbol for c in b.candidates)


def test_default_universe_policies_cover_all_phase1_tracks() -> None:
    from stockripper.tracks import DEFAULT_TRACKS

    for spec in DEFAULT_TRACKS:
        assert spec.track_id in DEFAULT_UNIVERSE_POLICIES, (
            f"track {spec.track_id} is missing a universe policy"
        )
