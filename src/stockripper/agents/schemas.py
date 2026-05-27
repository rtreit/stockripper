"""Strict pydantic schemas for every Phase-3 agent / judge output.

The whole point of these schemas is that *no* free-form LLM text is ever
turned into an order. Anything that does not validate is routed to a
quarantine queue (see :class:`AgentRunResult`).

Design notes:

- All money values are :class:`Decimal`. Prices and notionals are *never*
  stored as ``float`` here.
- All models are ``frozen=True, extra="forbid"`` so an unexpected field in
  an LLM payload reliably fails validation.
- ``RecommendationAction`` is the agent-facing action verb set from spec
  §7.3. ``OrderSide`` / ``OptionLegSide`` are the execution-facing enums
  used by the Phase-5 execution adapter; we define them here so that the
  Phase-3 schema-to-ledger mappers can already produce valid values.
"""

from __future__ import annotations

import datetime as dt
import hashlib
import uuid
from decimal import Decimal
from enum import StrEnum
from typing import Annotated, Any, Literal

from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    field_validator,
    model_validator,
)

from stockripper.data.provenance import Provenance
from stockripper.data.reasons import CandidateReason
from stockripper.data.universe_policy import InstrumentType as UniverseInstrumentType


# ---------------------------------------------------------------------------
# Common base
# ---------------------------------------------------------------------------
class _StrictModel(BaseModel):
    """Frozen, extra-forbidden Pydantic base used by every agent schema."""

    model_config = ConfigDict(extra="forbid", frozen=True, populate_by_name=True)


NonEmptyStr = Annotated[str, StringConstraints(strip_whitespace=True, min_length=1)]


# ---------------------------------------------------------------------------
# Instrument + action enums
# ---------------------------------------------------------------------------
class RecommendationInstrument(StrEnum):
    """Recommendation-level instrument shape.

    Distinct from :class:`stockripper.data.universe_policy.InstrumentType`
    (which gates universe eligibility per track). ``MULTI_LEG_OPTION`` is
    here because the execution adapter treats it as a single ticket;
    ``PAIR`` is here for pair-trade recommendations that the universe
    layer never sees.
    """

    EQUITY = "equity"
    ETF = "etf"
    LEVERAGED_ETF = "leveraged_etf"
    OPTION_SINGLE = "option_single"
    MULTI_LEG_OPTION = "multi_leg_option"
    PAIR = "pair"


class RecommendationAction(StrEnum):
    """Per-recommendation agent verbs (spec §7.3)."""

    BUY = "buy"
    SELL = "sell"
    SHORT = "short"
    COVER = "cover"
    BUY_TO_OPEN_OPTION = "buy_to_open_option"
    SELL_TO_OPEN_OPTION = "sell_to_open_option"
    MULTI_LEG = "multi_leg"
    AVOID = "avoid"
    HOLD = "hold"


_NON_TRADING_ACTIONS: frozenset[RecommendationAction] = frozenset(
    {RecommendationAction.AVOID, RecommendationAction.HOLD}
)


class OrderSide(StrEnum):
    """Execution-facing equity/ETF order side used by Phase-5 adapter."""

    BUY = "buy"
    SELL = "sell"
    SELL_SHORT = "sell_short"
    BUY_TO_COVER = "buy_to_cover"
    MULTI_LEG = "multi_leg"


class OptionLegSide(StrEnum):
    """Execution-facing option-leg side."""

    BUY_TO_OPEN = "buy_to_open"
    SELL_TO_OPEN = "sell_to_open"
    BUY_TO_CLOSE = "buy_to_close"
    SELL_TO_CLOSE = "sell_to_close"


class OptionRight(StrEnum):
    CALL = "call"
    PUT = "put"


class Severity(StrEnum):
    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"
    CRITICAL = "critical"


# ---------------------------------------------------------------------------
# Options structures
# ---------------------------------------------------------------------------
class OptionLeg(_StrictModel):
    """A single leg of a (possibly multi-leg) option recommendation."""

    underlying_symbol: NonEmptyStr
    occ_symbol: NonEmptyStr = Field(
        ..., description="OCC 21-char option symbol; canonical identifier for execution.",
    )
    right: OptionRight
    strike: Decimal = Field(..., gt=0)
    expiration_date: dt.date
    side: OptionLegSide
    ratio: int = Field(
        ..., ge=1, le=20,
        description="Number of contracts in this leg per unit of the multi-leg structure.",
    )


class MultiLegSpec(_StrictModel):
    """Multi-leg option structure (spread, condor, calendar, ...)."""

    label: NonEmptyStr = Field(..., description="Free-form label, e.g. 'long_call_vertical'.")
    legs: tuple[OptionLeg, ...] = Field(..., min_length=2, max_length=6)

    @field_validator("legs")
    @classmethod
    def _legs_share_underlying(cls, legs: tuple[OptionLeg, ...]) -> tuple[OptionLeg, ...]:
        underlyings = {leg.underlying_symbol.upper() for leg in legs}
        if len(underlyings) != 1:
            raise ValueError(
                f"multi-leg structures must share an underlying; got {sorted(underlyings)}"
            )
        return legs


class PairLeg(_StrictModel):
    """One leg of a pair-trade recommendation."""

    symbol: NonEmptyStr
    side: Literal["long", "short"]
    weight: Decimal = Field(..., gt=0, le=10)


# ---------------------------------------------------------------------------
# Evidence + prompt-injection findings
# ---------------------------------------------------------------------------
class EvidenceSourceType(StrEnum):
    SEC_FILING = "sec_filing"
    COMPANY_FUNDAMENTALS = "company_fundamentals"
    MARKET_DATA = "market_data"
    NEWS = "news"
    SOCIAL = "social"
    INTERNAL_DERIVED = "internal_derived"
    OTHER = "other"


class Evidence(_StrictModel):
    """One source-backed fact cited by an agent in support of a recommendation."""

    evidence_id: NonEmptyStr
    source_type: EvidenceSourceType
    source_url: str | None = Field(default=None)
    retrieved_at: dt.datetime
    claim: NonEmptyStr
    confidence: Decimal = Field(..., ge=0, le=1)
    raw_content_uri: str | None = Field(
        default=None,
        description="Pointer to the cached raw payload (e.g. .data-cache key); not the blob itself.",
    )
    content_hash: str = Field(
        ..., min_length=64, max_length=64,
        description="sha256 of the raw source payload (same hash discipline as Provenance).",
    )

    @classmethod
    def of_claim(
        cls,
        *,
        source_type: EvidenceSourceType,
        claim: str,
        confidence: Decimal | float,
        retrieved_at: dt.datetime,
        source_url: str | None = None,
        raw_content_uri: str | None = None,
        content_hash: str | None = None,
        evidence_id: str | None = None,
    ) -> Evidence:
        """Build an Evidence, hashing ``claim`` as a fallback content hash."""

        if content_hash is None:
            content_hash = hashlib.sha256(claim.encode("utf-8")).hexdigest()
        return cls(
            evidence_id=evidence_id or f"ev_{uuid.uuid4().hex[:16]}",
            source_type=source_type,
            source_url=source_url,
            retrieved_at=retrieved_at,
            claim=claim,
            confidence=Decimal(str(confidence)),
            raw_content_uri=raw_content_uri,
            content_hash=content_hash,
        )


class PromptInjectionFinding(_StrictModel):
    """One suspected prompt-injection signal in retrieved content."""

    pattern_id: NonEmptyStr
    severity: Severity
    snippet: str = Field(..., max_length=400)
    reason: NonEmptyStr
    detected_at: dt.datetime
    evidence_id: str | None = Field(
        default=None,
        description="Evidence record this finding came from, if any.",
    )


# ---------------------------------------------------------------------------
# Risk flags
# ---------------------------------------------------------------------------
class RiskFlagCode(StrEnum):
    """Structured codes used by RiskManager + agents to flag issues."""

    CONCENTRATION = "concentration"
    LIQUIDITY = "liquidity"
    LEVERAGE = "leverage"
    SHORT_INTEREST = "short_interest"
    OPTIONS_ASSIGNMENT = "options_assignment"
    EARNINGS_PROXIMITY = "earnings_proximity"
    EX_DIVIDEND = "ex_dividend"
    HALT_RISK = "halt_risk"
    POLICY_VIOLATION = "policy_violation"
    STALE_DATA = "stale_data"
    UNSUPPORTED_CLAIM = "unsupported_claim"
    PROMPT_INJECTION = "prompt_injection"
    OTHER = "other"


class RiskFlag(_StrictModel):
    code: RiskFlagCode
    severity: Severity
    description: NonEmptyStr
    params: dict[str, Any] = Field(default_factory=dict)


# ---------------------------------------------------------------------------
# Agent recommendation
# ---------------------------------------------------------------------------
class AgentRecommendation(_StrictModel):
    """Phase-3 agent output contract (spec §7.3).

    Either ``suggested_notional_usd`` or ``suggested_sizing_pct_of_equity``
    must be set when the action is a trading action; not both. ``evidence``
    is required for non-hold/avoid actions.
    """

    recommendation_id: NonEmptyStr
    agent_id: NonEmptyStr
    agent_version: NonEmptyStr
    track_id: NonEmptyStr
    symbol: NonEmptyStr
    instrument: RecommendationInstrument
    action: RecommendationAction
    conviction: Decimal = Field(..., ge=0, le=1)
    time_horizon_days: int = Field(..., ge=0, le=730)
    suggested_notional_usd: Decimal | None = Field(default=None, ge=0)
    suggested_sizing_pct_of_equity: Decimal | None = Field(default=None, ge=0, le=1)
    expected_return_pct: Decimal | None = None
    expected_drawdown_pct: Decimal | None = Field(default=None, ge=0, le=1)
    expected_holding_period_days: int | None = Field(default=None, ge=0, le=730)
    thesis: NonEmptyStr
    evidence: tuple[Evidence, ...] = Field(default_factory=tuple)
    risk_flags: tuple[RiskFlag, ...] = Field(default_factory=tuple)
    prompt_injection_findings: tuple[PromptInjectionFinding, ...] = Field(default_factory=tuple)
    multi_leg: MultiLegSpec | None = None
    pair_legs: tuple[PairLeg, ...] | None = None
    created_at: dt.datetime

    @model_validator(mode="after")
    def _sizing_xor(self) -> AgentRecommendation:
        # Hold/avoid don't size.
        if self.action in _NON_TRADING_ACTIONS:
            return self
        has_notional = self.suggested_notional_usd is not None
        has_pct = self.suggested_sizing_pct_of_equity is not None
        if has_notional == has_pct:  # both or neither
            raise ValueError(
                "Exactly one of suggested_notional_usd / suggested_sizing_pct_of_equity "
                "must be set for trading actions."
            )
        return self

    @model_validator(mode="after")
    def _evidence_required_for_trades(self) -> AgentRecommendation:
        if self.action in _NON_TRADING_ACTIONS:
            return self
        if not self.evidence:
            raise ValueError(f"Action {self.action.value!r} requires at least one Evidence record.")
        return self

    @model_validator(mode="after")
    def _multi_leg_shape(self) -> AgentRecommendation:
        if self.instrument == RecommendationInstrument.MULTI_LEG_OPTION:
            if self.multi_leg is None:
                raise ValueError("MULTI_LEG_OPTION recommendations must include multi_leg.")
            if self.action != RecommendationAction.MULTI_LEG:
                raise ValueError(
                    "MULTI_LEG_OPTION recommendations must use action=multi_leg."
                )
        elif self.multi_leg is not None:
            raise ValueError(
                f"multi_leg is only valid for instrument=multi_leg_option; got {self.instrument}."
            )
        if self.instrument == RecommendationInstrument.PAIR:
            if not self.pair_legs or len(self.pair_legs) != 2:
                raise ValueError("PAIR recommendations must include exactly two pair_legs.")
        elif self.pair_legs is not None:
            raise ValueError(
                f"pair_legs is only valid for instrument=pair; got {self.instrument}."
            )
        return self

    def universe_instrument(self) -> UniverseInstrumentType:
        """Map a recommendation instrument to the universe-eligibility enum."""

        mapping: dict[RecommendationInstrument, UniverseInstrumentType] = {
            RecommendationInstrument.EQUITY: UniverseInstrumentType.EQUITY_LONG,
            RecommendationInstrument.ETF: UniverseInstrumentType.ETF,
            RecommendationInstrument.LEVERAGED_ETF: UniverseInstrumentType.LEVERAGED_ETF,
            RecommendationInstrument.OPTION_SINGLE: UniverseInstrumentType.OPTION_SINGLE,
            RecommendationInstrument.MULTI_LEG_OPTION: UniverseInstrumentType.OPTION_SPREAD,
            # Pair trades don't have a universe instrument; they decompose
            # into equity longs and shorts at risk-gate time.
            RecommendationInstrument.PAIR: UniverseInstrumentType.EQUITY_LONG,
        }
        return mapping[self.instrument]


# ---------------------------------------------------------------------------
# Market climate (Market Climate Agent's output is not a recommendation)
# ---------------------------------------------------------------------------
class MarketRegime(StrEnum):
    BULL_TREND = "bull_trend"
    BEAR_TREND = "bear_trend"
    HIGH_VOL_CHOP = "high_vol_chop"
    LOW_VOL_GRIND = "low_vol_grind"
    CRISIS = "crisis"
    UNCERTAIN = "uncertain"


class MarketClimateReport(_StrictModel):
    """Shared regime/breadth/volatility snapshot consumed by judges + agents."""

    report_id: NonEmptyStr
    agent_id: NonEmptyStr
    agent_version: NonEmptyStr
    as_of: dt.date
    regime: MarketRegime
    breadth_score: Decimal = Field(..., ge=-1, le=1)
    volatility_score: Decimal = Field(..., ge=0, le=10)
    rates_backdrop: NonEmptyStr
    sector_rotation: tuple[NonEmptyStr, ...] = Field(default_factory=tuple)
    narrative: NonEmptyStr
    evidence: tuple[Evidence, ...] = Field(default_factory=tuple)
    created_at: dt.datetime


# ---------------------------------------------------------------------------
# Adversarial outputs
# ---------------------------------------------------------------------------
class SkepticCritique(_StrictModel):
    target_recommendation_id: NonEmptyStr
    severity: Severity
    issue_code: NonEmptyStr
    description: NonEmptyStr
    suggested_action: Literal["downweight", "veto", "request_more_evidence", "accept"]


class SkepticReport(_StrictModel):
    report_id: NonEmptyStr
    agent_id: NonEmptyStr
    agent_version: NonEmptyStr
    track_id: NonEmptyStr
    critiques: tuple[SkepticCritique, ...] = Field(default_factory=tuple)
    created_at: dt.datetime


class RiskAssessment(_StrictModel):
    target_recommendation_id: NonEmptyStr
    flags: tuple[RiskFlag, ...] = Field(default_factory=tuple)
    structural_notes: NonEmptyStr


class RiskManagerReport(_StrictModel):
    report_id: NonEmptyStr
    agent_id: NonEmptyStr
    agent_version: NonEmptyStr
    track_id: NonEmptyStr
    assessments: tuple[RiskAssessment, ...] = Field(default_factory=tuple)
    portfolio_level_flags: tuple[RiskFlag, ...] = Field(default_factory=tuple)
    created_at: dt.datetime


class PromptInjectionReport(_StrictModel):
    report_id: NonEmptyStr
    agent_id: NonEmptyStr
    agent_version: NonEmptyStr
    findings: tuple[PromptInjectionFinding, ...] = Field(default_factory=tuple)
    scanned_evidence_ids: tuple[NonEmptyStr, ...] = Field(default_factory=tuple)
    created_at: dt.datetime

    @property
    def highest_severity(self) -> Severity | None:
        if not self.findings:
            return None
        order = (Severity.LOW, Severity.MEDIUM, Severity.HIGH, Severity.CRITICAL)
        rank = {s: i for i, s in enumerate(order)}
        return max(self.findings, key=lambda f: rank[f.severity]).severity


# ---------------------------------------------------------------------------
# Judge outputs
# ---------------------------------------------------------------------------
class PortfolioPosture(StrEnum):
    AGGRESSIVE_LONG = "aggressive_long"
    NET_LONG = "net_long"
    BALANCED = "balanced"
    NET_SHORT = "net_short"
    AGGRESSIVE_SHORT = "aggressive_short"
    DEFENSIVE = "defensive"
    CASH = "cash"


class ActionOrderType(StrEnum):
    MARKET = "market"
    LIMIT = "limit"
    STOP = "stop"
    STOP_LIMIT = "stop_limit"


class ActionItem(_StrictModel):
    """One executable action coming out of a judge's plan (mirrors decision_actions)."""

    action_id: NonEmptyStr
    track_id: NonEmptyStr
    symbol: NonEmptyStr
    instrument: RecommendationInstrument
    side: OrderSide
    target_notional_usd: Decimal | None = Field(default=None, ge=0)
    target_pct_equity: Decimal | None = Field(default=None, ge=0, le=1)
    order_type: ActionOrderType = ActionOrderType.MARKET
    limit_price: Decimal | None = Field(default=None, gt=0)
    stop_price: Decimal | None = Field(default=None, gt=0)
    time_in_force: Literal["day", "gtc", "ioc", "fok"] = "day"
    multi_leg: MultiLegSpec | None = None
    pair_legs: tuple[PairLeg, ...] | None = None
    rationale: NonEmptyStr
    contributing_recommendation_ids: tuple[NonEmptyStr, ...] = Field(default_factory=tuple)

    @model_validator(mode="after")
    def _sizing_xor(self) -> ActionItem:
        has_notional = self.target_notional_usd is not None
        has_pct = self.target_pct_equity is not None
        if has_notional == has_pct:
            raise ValueError(
                "ActionItem must set exactly one of target_notional_usd / target_pct_equity."
            )
        return self

    @model_validator(mode="after")
    def _order_type_consistency(self) -> ActionItem:
        if (
            self.order_type in {ActionOrderType.LIMIT, ActionOrderType.STOP_LIMIT}
            and self.limit_price is None
        ):
            raise ValueError(f"{self.order_type} requires limit_price.")
        if (
            self.order_type in {ActionOrderType.STOP, ActionOrderType.STOP_LIMIT}
            and self.stop_price is None
        ):
            raise ValueError(f"{self.order_type} requires stop_price.")
        if self.side == OrderSide.MULTI_LEG and self.multi_leg is None:
            raise ValueError("OrderSide.MULTI_LEG requires multi_leg structure.")
        return self


class ActionPlan(_StrictModel):
    """A judge's plan for a single track/window (mirrors judge_decisions)."""

    decision_id: NonEmptyStr
    track_id: NonEmptyStr
    judge_agent_id: NonEmptyStr
    judge_agent_version: NonEmptyStr
    portfolio_posture: PortfolioPosture
    items: tuple[ActionItem, ...] = Field(default_factory=tuple)
    rationale: NonEmptyStr
    objective_label: NonEmptyStr = Field(
        ..., description="e.g. 'maximize_calmar', 'maximize_raw_return'.",
    )
    created_at: dt.datetime


class JudgeDecision(_StrictModel):
    """Envelope returned by a judge: plan + plan-level commentary + provenance."""

    plan: ActionPlan
    provenance: Provenance | None = None


# ---------------------------------------------------------------------------
# Agent run lifecycle
# ---------------------------------------------------------------------------
class AgentRunStatus(StrEnum):
    OK = "ok"
    QUARANTINED = "quarantined"
    SKIPPED = "skipped"
    DETECTOR_BLOCKED = "detector_blocked"


class CandidateEvidenceRef(_StrictModel):
    """Pointer to one piece of supporting evidence for a candidate symbol."""

    source_type: EvidenceSourceType
    source_url: str | None = None
    raw_content_uri: str | None = None
    content_hash: str = Field(..., min_length=64, max_length=64)
    retrieved_at: dt.datetime
    summary: NonEmptyStr


class EvidencePacket(_StrictModel):
    """Serializable input bundle for every agent.

    Carries the candidate identity, the candidate-reason codes that landed
    it in this track's universe, sanitized evidence references (URI +
    content hash, *not* raw blobs), per-source provenance, and the
    pre-LLM prompt-injection report. LangGraph checkpoints stay light
    because raw content is referenced, not embedded.
    """

    packet_id: NonEmptyStr
    track_id: NonEmptyStr
    window_id: NonEmptyStr
    symbol: NonEmptyStr
    instrument: RecommendationInstrument
    candidate_reasons: tuple[CandidateReason, ...] = Field(default_factory=tuple)
    snapshot_summary: NonEmptyStr
    evidence_refs: tuple[CandidateEvidenceRef, ...] = Field(default_factory=tuple)
    provenances: tuple[Provenance, ...] = Field(default_factory=tuple)
    prompt_injection_report: PromptInjectionReport | None = None
    as_of: dt.datetime


class AgentRunInput(_StrictModel):
    """Fully-serializable input passed to ``BaseAgent.run``.

    Runtime objects (LLM clients, clocks, registries) are NOT in here —
    they are injected as ``BaseAgent.run(..., llm=...)`` kwargs so this
    object is safe to checkpoint and replay.
    """

    run_id: NonEmptyStr
    track_id: NonEmptyStr
    window_id: NonEmptyStr
    agent_id: NonEmptyStr
    packet: EvidencePacket
    council_outputs: tuple[AgentRecommendation, ...] = Field(default_factory=tuple)
    market_climate: MarketClimateReport | None = None
    skeptic_report: SkepticReport | None = None
    risk_manager_report: RiskManagerReport | None = None
    rng_seed: int | None = None
    created_at: dt.datetime


class RequestFingerprint(_StrictModel):
    """Everything that should reproduce the same LLM response under stable models."""

    model_id: NonEmptyStr
    temperature: Decimal = Field(default=Decimal("1"), ge=0, le=2)
    top_p: Decimal = Field(default=Decimal("1"), ge=0, le=1)
    prompt_content_hash: str = Field(..., min_length=64, max_length=64)
    schema_content_hash: str = Field(..., min_length=64, max_length=64)
    input_content_hash: str = Field(..., min_length=64, max_length=64)
    seed: int | None = None

    @property
    def digest(self) -> str:
        body = (
            f"{self.model_id}|{self.temperature}|{self.top_p}|"
            f"{self.prompt_content_hash}|{self.schema_content_hash}|"
            f"{self.input_content_hash}|{self.seed}"
        )
        return hashlib.sha256(body.encode("utf-8")).hexdigest()


# A "fake" fingerprint for non-LLM agents (rule-based planners, regex PI
# detector, etc) so the AgentRunResult shape is uniform.
_RULE_BASED_FINGERPRINT_PROMPT_HASH = "0" * 64


def rule_based_fingerprint(*, agent_id: str, input_payload: Any) -> RequestFingerprint:
    """Fingerprint factory used by deterministic non-LLM agents."""

    import json

    raw = json.dumps(input_payload, sort_keys=True, default=str, separators=(",", ":"))
    input_hash = hashlib.sha256(raw.encode("utf-8")).hexdigest()
    return RequestFingerprint(
        model_id=f"rule:{agent_id}",
        temperature=Decimal("0"),
        top_p=Decimal("1"),
        prompt_content_hash=_RULE_BASED_FINGERPRINT_PROMPT_HASH,
        schema_content_hash=_RULE_BASED_FINGERPRINT_PROMPT_HASH,
        input_content_hash=input_hash,
        seed=None,
    )


AgentOutput = (
    AgentRecommendation
    | MarketClimateReport
    | SkepticReport
    | RiskManagerReport
    | PromptInjectionReport
    | JudgeDecision
)


class AgentRunResult(_StrictModel):
    """Uniform result envelope from every agent / judge call.

    Never raise on validation failure inside ``BaseAgent.run`` — return
    ``status=QUARANTINED`` instead, so a single bad output cannot crash a
    LangGraph run.
    """

    run_id: NonEmptyStr
    agent_id: NonEmptyStr
    agent_version: NonEmptyStr
    track_id: NonEmptyStr
    status: AgentRunStatus
    fingerprint: RequestFingerprint
    output: AgentOutput | None = None
    raw_response_text: str | None = Field(
        default=None,
        description="Verbatim LLM response body — empty for rule-based agents.",
    )
    quarantine_reason: str | None = None
    latency_ms: int | None = Field(default=None, ge=0)
    created_at: dt.datetime


# ---------------------------------------------------------------------------
# Ledger mapping helpers (used by Phase 4 persistence; tests cover them here)
# ---------------------------------------------------------------------------
def recommendation_to_ledger_row(rec: AgentRecommendation) -> dict[str, Any]:
    """Project an :class:`AgentRecommendation` into a dict shaped like the
    ``recommendations`` SQL table (spec §15.2).
    """

    return {
        "recommendation_id": rec.recommendation_id,
        "track_id": rec.track_id,
        "agent_id": rec.agent_id,
        "symbol": rec.symbol,
        "instrument_type": rec.instrument.value,
        "action": rec.action.value,
        "conviction": rec.conviction,
        "time_horizon_days": rec.time_horizon_days,
        "suggested_notional_usd": rec.suggested_notional_usd,
        "suggested_pct_equity": rec.suggested_sizing_pct_of_equity,
        "expected_return_pct": rec.expected_return_pct,
        "max_expected_drawdown_pct": rec.expected_drawdown_pct,
        "thesis": rec.thesis,
        "schema_valid": True,
        "created_at": rec.created_at,
    }


def action_item_to_ledger_row(
    item: ActionItem, *, decision_id: str,
) -> dict[str, Any]:
    """Project an :class:`ActionItem` into a dict shaped like ``decision_actions``."""

    return {
        "action_id": item.action_id,
        "decision_id": decision_id,
        "track_id": item.track_id,
        "symbol": item.symbol,
        "instrument_type": item.instrument.value,
        "action": item.side.value,
        "target_notional_usd": item.target_notional_usd,
        "target_pct_equity": item.target_pct_equity,
        "order_type": item.order_type.value,
        "limit_price": item.limit_price,
        "stop_price": item.stop_price,
        "time_in_force": item.time_in_force,
        "leg_json": [leg.model_dump(mode="json") for leg in item.multi_leg.legs]
        if item.multi_leg is not None
        else None,
        "rationale": item.rationale,
    }


__all__ = (
    "ActionItem",
    "ActionOrderType",
    "ActionPlan",
    "AgentOutput",
    "AgentRecommendation",
    "AgentRunInput",
    "AgentRunResult",
    "AgentRunStatus",
    "CandidateEvidenceRef",
    "Evidence",
    "EvidencePacket",
    "EvidenceSourceType",
    "JudgeDecision",
    "MarketClimateReport",
    "MarketRegime",
    "MultiLegSpec",
    "OptionLeg",
    "OptionLegSide",
    "OptionRight",
    "OrderSide",
    "PairLeg",
    "PortfolioPosture",
    "PromptInjectionFinding",
    "PromptInjectionReport",
    "RecommendationAction",
    "RecommendationInstrument",
    "RequestFingerprint",
    "RiskAssessment",
    "RiskFlag",
    "RiskFlagCode",
    "RiskManagerReport",
    "Severity",
    "SkepticCritique",
    "SkepticReport",
    "action_item_to_ledger_row",
    "recommendation_to_ledger_row",
    "rule_based_fingerprint",
)
