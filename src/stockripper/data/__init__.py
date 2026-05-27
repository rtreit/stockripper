"""Data ingestion adapters (market data, SEC EDGAR, fundamentals, news) and
the universe builder.

This package is the Phase 2 "research" surface — research workers and
candidate-universe construction. It is intentionally **non-order-capable**:
no module under :mod:`stockripper.data` may construct or invoke an
Alpaca trading client. Order execution lives elsewhere (Phase 5).
"""

from stockripper.data.provenance import Provenance
from stockripper.data.reasons import CandidateReason, CandidateReasonCode
from stockripper.data.universe import (
    AssetRecord,
    AssetSnapshot,
    Candidate,
    SnapshotProvider,
    UniverseBuilder,
    UniverseBuildRequest,
    UniverseBuildResult,
)
from stockripper.data.universe_policy import (
    DEFAULT_UNIVERSE_POLICIES,
    InstrumentType,
    MarketCapBand,
    UniversePolicyParams,
)

__all__ = (
    "DEFAULT_UNIVERSE_POLICIES",
    "AssetRecord",
    "AssetSnapshot",
    "Candidate",
    "CandidateReason",
    "CandidateReasonCode",
    "InstrumentType",
    "MarketCapBand",
    "Provenance",
    "SnapshotProvider",
    "UniverseBuildRequest",
    "UniverseBuildResult",
    "UniverseBuilder",
    "UniversePolicyParams",
)
