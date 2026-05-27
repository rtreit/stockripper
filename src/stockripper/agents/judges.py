"""Per-track judges.

One :class:`JudgeAgent` per strategy track (spec §8). Each is wired to a
distinct prompt template (``judge.yolo``, ``judge.conservative``, ...)
and emits a :class:`JudgeDecision` containing a single :class:`ActionPlan`.

The judge prompt always sees:
- council recommendations
- skeptic report
- risk manager report
- market climate report
- the original evidence packet (so it can re-verify a claim if needed)
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Final

from stockripper.agents.base import BaseAgent
from stockripper.agents.prompts import (
    AGGRESSIVE_JUDGE_CORE,
    BALANCED_JUDGE_CORE,
    CONCENTRATED_JUDGE_CORE,
    CONSERVATIVE_JUDGE_CORE,
    YOLO_JUDGE_CORE,
)
from stockripper.agents.schemas import (
    AgentRunInput,
    JudgeDecision,
)


@dataclass(frozen=True)
class JudgeSpec:
    judge_id: str
    track_id: str
    prompt_template_id: str
    objective_label: str


JUDGES: Final[tuple[JudgeSpec, ...]] = (
    JudgeSpec("judge_yolo", "yolo", YOLO_JUDGE_CORE.template_id, "maximize_raw_return"),
    JudgeSpec("judge_conservative", "conservative", CONSERVATIVE_JUDGE_CORE.template_id, "maximize_calmar"),
    JudgeSpec("judge_balanced", "balanced", BALANCED_JUDGE_CORE.template_id, "maximize_sharpe"),
    JudgeSpec("judge_aggressive", "aggressive", AGGRESSIVE_JUDGE_CORE.template_id, "maximize_sortino"),
    JudgeSpec(
        "judge_concentrated",
        "concentrated",
        CONCENTRATED_JUDGE_CORE.template_id,
        "maximize_information_ratio",
    ),
)


def _format_skeptic(payload: AgentRunInput) -> str:
    sk = payload.skeptic_report
    if sk is None or not sk.critiques:
        return "Skeptic critiques: none."
    lines = ["Skeptic critiques:"]
    for c in sk.critiques:
        lines.append(
            f"  - rec={c.target_recommendation_id} severity={c.severity.value} "
            f"code={c.issue_code} action={c.suggested_action} :: {c.description[:200]}"
        )
    return "\n".join(lines)


def _format_risk(payload: AgentRunInput) -> str:
    rm = payload.risk_manager_report
    if rm is None or (not rm.assessments and not rm.portfolio_level_flags):
        return "Risk manager: no flags."
    lines = ["Risk manager assessments:"]
    for a in rm.assessments:
        flag_codes = ", ".join(f.code.value for f in a.flags) or "no_flags"
        lines.append(
            f"  - rec={a.target_recommendation_id} flags=[{flag_codes}] :: {a.structural_notes[:200]}"
        )
    if rm.portfolio_level_flags:
        codes = ", ".join(f.code.value for f in rm.portfolio_level_flags)
        lines.append(f"  - portfolio_level: [{codes}]")
    return "\n".join(lines)


def _format_climate(payload: AgentRunInput) -> str:
    mc = payload.market_climate
    if mc is None:
        return "Market climate: not supplied."
    return (
        f"Market climate: regime={mc.regime.value} breadth={mc.breadth_score} "
        f"vol={mc.volatility_score} rates_backdrop={mc.rates_backdrop} "
        f"narrative={mc.narrative[:200]}"
    )


def _format_council(payload: AgentRunInput) -> str:
    if not payload.council_outputs:
        return "Council recommendations: none."
    lines = ["Council recommendations:"]
    for i, rec in enumerate(payload.council_outputs):
        sizing = (
            f"${rec.suggested_notional_usd}"
            if rec.suggested_notional_usd is not None
            else (
                f"{rec.suggested_sizing_pct_of_equity}*equity"
                if rec.suggested_sizing_pct_of_equity is not None
                else "(no size)"
            )
        )
        lines.append(
            f"  - rec#{i} id={rec.recommendation_id} agent={rec.agent_id} "
            f"symbol={rec.symbol} action={rec.action.value} "
            f"instrument={rec.instrument.value} conv={rec.conviction} "
            f"size={sizing} horizon={rec.time_horizon_days}d"
        )
    return "\n".join(lines)


class JudgeAgent(BaseAgent[JudgeDecision]):
    """Per-track judge. Aggregates council + adversarial outputs into a plan."""

    agent_version = "1.0.0"
    output_schema = JudgeDecision

    def __init__(self, spec: JudgeSpec) -> None:
        self.spec = spec
        self.agent_id = spec.judge_id
        self.prompt_template_id = spec.prompt_template_id

    def render_user_message(self, payload: AgentRunInput) -> str:
        return (
            f"track_id: {payload.track_id}\n"
            f"window_id: {payload.window_id}\n"
            f"objective: {self.spec.objective_label}\n"
            f"\n{_format_council(payload)}\n"
            f"\n{_format_skeptic(payload)}\n"
            f"\n{_format_risk(payload)}\n"
            f"\n{_format_climate(payload)}\n"
            f"\nReturn a JudgeDecision whose plan uses objective_label='{self.spec.objective_label}'.\n"
        )


def make_judges() -> tuple[JudgeAgent, ...]:
    return tuple(JudgeAgent(spec) for spec in JUDGES)


__all__ = ("JUDGES", "JudgeAgent", "JudgeSpec", "make_judges")
