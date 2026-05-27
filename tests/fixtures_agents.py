"""Phase-3 shared test fixtures."""

from __future__ import annotations

import datetime as dt
import uuid
from decimal import Decimal

import pytest

from stockripper.agents.evidence import build_evidence_packet
from stockripper.agents.schemas import (
    AgentRecommendation,
    AgentRunInput,
    Evidence,
    EvidencePacket,
    EvidenceSourceType,
    MarketClimateReport,
    MarketRegime,
    RecommendationAction,
    RecommendationInstrument,
    RiskAssessment,
    RiskManagerReport,
    Severity,
    SkepticCritique,
    SkepticReport,
)
from stockripper.data.reasons import CandidateReason, CandidateReasonCode
from stockripper.data.universe import AssetSnapshot, Candidate


def _now() -> dt.datetime:
    return dt.datetime(2026, 7, 1, 14, 0, tzinfo=dt.UTC)


@pytest.fixture
def now() -> dt.datetime:
    return _now()


@pytest.fixture
def sample_snapshot() -> AssetSnapshot:
    return AssetSnapshot(
        symbol="AAPL",
        last_price=Decimal("190.5"),
        adv_usd_20d=Decimal("1500000000"),
        market_cap_usd=Decimal("3000000000000"),
        recent_8k_within_days=3,
        recent_news_count_30d=12,
    )


@pytest.fixture
def sample_candidate(sample_snapshot: AssetSnapshot) -> Candidate:
    return Candidate(
        symbol="AAPL",
        bucket="core",
        reasons=(
            CandidateReason(
                code=CandidateReasonCode.PASSES_ADV_FLOOR,
                params={"adv_usd_20d": "1.5e9", "instrument": "equity_long"},
            ),
            CandidateReason(
                code=CandidateReasonCode.HAS_RECENT_8K,
                params={"recent_8k_within_days": "3"},
            ),
        ),
        snapshot=sample_snapshot,
    )


@pytest.fixture
def sample_packet(sample_candidate: Candidate) -> EvidencePacket:
    return build_evidence_packet(
        track_id="balanced",
        window_id="2026-07-01T14",
        candidate=sample_candidate,
        evidence_excerpts=(
            (
                EvidenceSourceType.NEWS,
                "https://example.test/aapl-pro-launch",
                "Apple announces successor to its Pro lineup with stronger guidance.",
            ),
            (
                EvidenceSourceType.SEC_FILING,
                "https://example.test/aapl-8k",
                "Form 8-K: Material agreement signed; expected to add to FY revenue.",
            ),
        ),
        now=_now(),
    )


@pytest.fixture
def sample_recommendation() -> AgentRecommendation:
    return AgentRecommendation(
        recommendation_id=f"rec_{uuid.uuid4().hex[:16]}",
        agent_id="quality",
        agent_version="1.0.0",
        track_id="balanced",
        symbol="AAPL",
        instrument=RecommendationInstrument.EQUITY,
        action=RecommendationAction.BUY,
        conviction=Decimal("0.7"),
        time_horizon_days=180,
        suggested_notional_usd=Decimal("10000"),
        suggested_sizing_pct_of_equity=None,
        expected_return_pct=Decimal("0.12"),
        expected_drawdown_pct=Decimal("0.08"),
        expected_holding_period_days=180,
        thesis="High-quality compounder with durable margins and shareholder-friendly capital return.",
        evidence=(
            Evidence.of_claim(
                claim="Apple operating margin trends above 29% TTM",
                source_type=EvidenceSourceType.COMPANY_FUNDAMENTALS,
                source_url="https://example.test/aapl-fundamentals",
                retrieved_at=_now(),
                confidence=0.7,
            ),
        ),
        risk_flags=(),
        prompt_injection_findings=(),
        multi_leg=None,
        pair_legs=None,
        created_at=_now(),
    )


@pytest.fixture
def sample_market_climate() -> MarketClimateReport:
    return MarketClimateReport(
        report_id=f"climate_{uuid.uuid4().hex[:16]}",
        agent_id="market_climate",
        agent_version="1.0.0",
        as_of=_now().date(),
        regime=MarketRegime.BULL_TREND,
        breadth_score=Decimal("0.4"),
        volatility_score=Decimal("1.2"),
        rates_backdrop="restrictive_but_easing",
        sector_rotation=("tech", "consumer_disc"),
        narrative="Trend up with broadening participation across megacaps.",
        evidence=(),
        created_at=_now(),
    )


@pytest.fixture
def sample_skeptic_report(sample_recommendation: AgentRecommendation) -> SkepticReport:
    return SkepticReport(
        report_id=f"skeptic_{uuid.uuid4().hex[:16]}",
        agent_id="skeptic",
        agent_version="1.0.0",
        track_id="balanced",
        critiques=(
            SkepticCritique(
                target_recommendation_id=sample_recommendation.recommendation_id,
                severity=Severity.LOW,
                issue_code="missing_source",
                description="Thesis cites margin trend but does not link to filing year-by-year.",
                suggested_action="request_more_evidence",
            ),
        ),
        created_at=_now(),
    )


@pytest.fixture
def sample_risk_report(sample_recommendation: AgentRecommendation) -> RiskManagerReport:
    return RiskManagerReport(
        report_id=f"riskmgr_{uuid.uuid4().hex[:16]}",
        agent_id="risk_manager",
        agent_version="1.0.0",
        track_id="balanced",
        assessments=(
            RiskAssessment(
                target_recommendation_id=sample_recommendation.recommendation_id,
                flags=(),
                structural_notes="Liquid, single-name long; within concentration policy.",
            ),
        ),
        portfolio_level_flags=(),
        created_at=_now(),
    )


@pytest.fixture
def sample_run_input(
    sample_packet: EvidencePacket,
    sample_recommendation: AgentRecommendation,
    sample_market_climate: MarketClimateReport,
    sample_skeptic_report: SkepticReport,
    sample_risk_report: RiskManagerReport,
) -> AgentRunInput:
    return AgentRunInput(
        run_id=f"run_{uuid.uuid4().hex[:16]}",
        track_id="balanced",
        window_id="2026-07-01T14",
        agent_id="quality",
        packet=sample_packet,
        council_outputs=(sample_recommendation,),
        market_climate=sample_market_climate,
        skeptic_report=sample_skeptic_report,
        risk_manager_report=sample_risk_report,
        rng_seed=7,
        created_at=_now(),
    )
