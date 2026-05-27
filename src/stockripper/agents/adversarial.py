"""Adversarial agents: skeptic, risk manager, prompt-injection detector.

These three are run AFTER the council and BEFORE the judge. They never
emit orders. Their outputs feed into the judge's prompt so it can
discount or veto weak recommendations.
"""

from __future__ import annotations

import datetime as dt
import uuid

from stockripper.agents.base import BaseAgent
from stockripper.agents.prompt_injection import scan_evidence
from stockripper.agents.prompts import (  # noqa: F401 — eager register
    RISK_MANAGER_CORE,
    SKEPTIC_CORE,
)
from stockripper.agents.schemas import (
    AgentRunInput,
    PromptInjectionReport,
    RiskAssessment,
    RiskManagerReport,
    SkepticCritique,
    SkepticReport,
)


def _now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def _summarize_recommendation(rec, idx: int) -> str:  # type: ignore[no-untyped-def]
    return (
        f"- rec#{idx} id={rec.recommendation_id} agent={rec.agent_id} "
        f"symbol={rec.symbol} action={rec.action.value} "
        f"instrument={rec.instrument.value} conviction={rec.conviction} "
        f"thesis={rec.thesis[:240]}"
    )


def _format_recommendations_block(payload: AgentRunInput) -> str:
    if not payload.council_outputs:
        return "(no council recommendations supplied)"
    return "\n".join(
        _summarize_recommendation(rec, i) for i, rec in enumerate(payload.council_outputs)
    )


# ---------------------------------------------------------------------------
# Skeptic
# ---------------------------------------------------------------------------
class SkepticAgent(BaseAgent[SkepticReport]):
    """LLM-backed devil's-advocate critique of every council recommendation."""

    agent_id = "skeptic"
    agent_version = "1.0.0"
    prompt_template_id = "adversarial.skeptic"
    output_schema = SkepticReport

    def render_user_message(self, payload: AgentRunInput) -> str:
        return (
            f"track_id: {payload.track_id}\n"
            f"window_id: {payload.window_id}\n"
            f"symbol_context: {payload.packet.symbol}\n"
            f"\nCouncil recommendations under review:\n"
            f"{_format_recommendations_block(payload)}\n"
            f"\nUse the supplied evidence packet to ground or refute claims.\n"
        )


def empty_skeptic_report(*, track_id: str, now: dt.datetime | None = None) -> SkepticReport:
    """Return an empty SkepticReport (used when no LLM client is wired)."""

    when = now if now is not None else _now()
    return SkepticReport(
        report_id=f"skeptic_{uuid.uuid4().hex[:16]}",
        agent_id="skeptic",
        agent_version="1.0.0",
        track_id=track_id,
        critiques=(),
        created_at=when,
    )


# ---------------------------------------------------------------------------
# Risk Manager
# ---------------------------------------------------------------------------
class RiskManagerAgent(BaseAgent[RiskManagerReport]):
    """LLM-backed structural-risk commentary. Does NOT approve trades."""

    agent_id = "risk_manager"
    agent_version = "1.0.0"
    prompt_template_id = "adversarial.risk_manager"
    output_schema = RiskManagerReport

    def render_user_message(self, payload: AgentRunInput) -> str:
        return (
            f"track_id: {payload.track_id}\n"
            f"window_id: {payload.window_id}\n"
            f"\nCouncil recommendations under review:\n"
            f"{_format_recommendations_block(payload)}\n"
            f"\nProduce a RiskAssessment for each recommendation. "
            f"Use the structured RiskFlagCode taxonomy.\n"
        )


def empty_risk_manager_report(
    *, track_id: str, now: dt.datetime | None = None
) -> RiskManagerReport:
    when = now if now is not None else _now()
    return RiskManagerReport(
        report_id=f"riskmgr_{uuid.uuid4().hex[:16]}",
        agent_id="risk_manager",
        agent_version="1.0.0",
        track_id=track_id,
        assessments=(),
        portfolio_level_flags=(),
        created_at=when,
    )


# ---------------------------------------------------------------------------
# Prompt-injection detector
# ---------------------------------------------------------------------------
class PromptInjectionDetector:
    """Thin agent-shaped wrapper around the regex baseline.

    Lives outside :class:`BaseAgent` because it doesn't call an LLM and
    its output type (``PromptInjectionReport``) is built directly from
    pre-sanitized evidence text rather than from a prompt rendering.
    """

    agent_id = "prompt_injection_detector"
    agent_version = "1.0.0"

    def scan(
        self,
        evidence: tuple[tuple[str, str], ...],
        *,
        track_id: str = "shared",
        now: dt.datetime | None = None,
    ) -> PromptInjectionReport:
        return scan_evidence(evidence, track_id=track_id, now=now)


__all__ = (
    "PromptInjectionDetector",
    "RiskAssessment",
    "RiskManagerAgent",
    "SkepticAgent",
    "SkepticCritique",
    "empty_risk_manager_report",
    "empty_skeptic_report",
)
