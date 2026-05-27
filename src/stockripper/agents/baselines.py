"""Deterministic non-LLM planners for the baseline tracks.

The ``quant_signal``, ``random_baseline``, and ``benchmark`` tracks are
explicit non-LLM tracks (spec §7.2). Their "judge" is a pure-Python
planner so we can score every LLM-driven track against a trio of cheap,
auditable references.

All three planners produce the same :class:`JudgeDecision` envelope as
the LLM judges, so the registry and the dashboard treat every track
uniformly.
"""

from __future__ import annotations

import datetime as dt
import random
import uuid
from decimal import Decimal

from pydantic import BaseModel

from stockripper.agents.base import BaseAgent
from stockripper.agents.prompts import PROMPTS, PromptTemplate
from stockripper.agents.schemas import (
    ActionItem,
    ActionOrderType,
    ActionPlan,
    AgentRunInput,
    JudgeDecision,
    OrderSide,
    PortfolioPosture,
    RecommendationInstrument,
)

# ---------------------------------------------------------------------------
# Prompt templates (still registered for content-hash auditability even
# though baselines never send them to an LLM).
# ---------------------------------------------------------------------------
QUANT_SIGNAL_TEMPLATE = PROMPTS.register(
    PromptTemplate(
        template_id="baseline.quant_signal",
        version="1.0.0",
        body=(
            "Deterministic quant baseline.\n"
            "Buy ordering: lowest-instrument-rank wins, then highest conviction, "
            "then alphabetical symbol. Equal-weight across the top 5 candidates."
        ),
    )
)

RANDOM_BASELINE_TEMPLATE = PROMPTS.register(
    PromptTemplate(
        template_id="baseline.random_baseline",
        version="1.0.0",
        body=(
            "Random baseline. Seeded RNG selects up to 3 candidates uniformly at "
            "random and equal-weights them. Pure noise reference."
        ),
    )
)

BENCHMARK_TEMPLATE = PROMPTS.register(
    PromptTemplate(
        template_id="baseline.benchmark",
        version="1.0.0",
        body=(
            "Benchmark baseline. Always hold a single fixed equity index ETF at "
            "100% target weight. Provides a buy-and-hold reference curve."
        ),
    )
)


# ---------------------------------------------------------------------------
# Shared helpers
# ---------------------------------------------------------------------------
def _now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def _side_for(instrument: RecommendationInstrument) -> OrderSide:
    # Baselines only ever go long; options-only candidates aren't passed here.
    del instrument
    return OrderSide.BUY


def _decision(
    *,
    judge_id: str,
    track_id: str,
    items: tuple[ActionItem, ...],
    objective_label: str,
    posture: PortfolioPosture,
    rationale: str,
    now: dt.datetime | None = None,
) -> JudgeDecision:
    when = now if now is not None else _now()
    plan = ActionPlan(
        decision_id=f"plan_{uuid.uuid4().hex[:16]}",
        track_id=track_id,
        judge_agent_id=judge_id,
        judge_agent_version="1.0.0",
        portfolio_posture=posture,
        items=items,
        rationale=rationale,
        objective_label=objective_label,
        created_at=when,
    )
    return JudgeDecision(plan=plan, provenance=None)


# ---------------------------------------------------------------------------
# Quant signal baseline — top-5 by conviction, equal weight 20%.
# ---------------------------------------------------------------------------
class QuantSignalAgent(BaseAgent[JudgeDecision]):
    agent_id = "quant_signal_stacker"
    agent_version = "1.0.0"
    prompt_template_id = QUANT_SIGNAL_TEMPLATE.template_id
    output_schema = JudgeDecision
    requires_llm = False

    def render_user_message(self, payload: AgentRunInput) -> str:
        return f"track={payload.track_id} window={payload.window_id}"

    def run_local(self, payload: AgentRunInput) -> JudgeDecision:
        # Rank tradable recommendations by conviction descending, then symbol.
        tradable = [
            r
            for r in payload.council_outputs
            if r.action.value in {"buy"}
            and r.instrument
            in {
                RecommendationInstrument.EQUITY,
                RecommendationInstrument.ETF,
                RecommendationInstrument.LEVERAGED_ETF,
            }
        ]
        tradable.sort(key=lambda r: (-float(r.conviction), r.symbol))
        top = tradable[:5]
        weight = Decimal("0.20") if top else Decimal("0")
        items: list[ActionItem] = []
        for rec in top:
            items.append(
                ActionItem(
                    action_id=f"act_{uuid.uuid4().hex[:16]}",
                    track_id=payload.track_id,
                    symbol=rec.symbol,
                    instrument=rec.instrument,
                    side=_side_for(rec.instrument),
                    target_notional_usd=None,
                    target_pct_equity=weight,
                    order_type=ActionOrderType.MARKET,
                    limit_price=None,
                    stop_price=None,
                    time_in_force="day",
                    multi_leg=None,
                    pair_legs=None,
                    rationale=f"Top-5 equal-weight from council conviction (rank={rec.conviction}).",
                    contributing_recommendation_ids=(rec.recommendation_id,),
                )
            )
        return _decision(
            judge_id=self.agent_id,
            track_id=payload.track_id,
            items=tuple(items),
            objective_label="maximize_quant_signal",
            posture=PortfolioPosture.NET_LONG if items else PortfolioPosture.CASH,
            rationale=(
                f"Deterministic quant baseline: top {len(items)} buy candidates by "
                f"conviction, equal-weighted at {weight}."
            ),
        )


# ---------------------------------------------------------------------------
# Random baseline — seeded RNG picks up to 3 candidates.
# ---------------------------------------------------------------------------
class RandomBaselineAgent(BaseAgent[JudgeDecision]):
    agent_id = "random_baseline"
    agent_version = "1.0.0"
    prompt_template_id = RANDOM_BASELINE_TEMPLATE.template_id
    output_schema = JudgeDecision
    requires_llm = False

    def render_user_message(self, payload: AgentRunInput) -> str:
        return f"track={payload.track_id} window={payload.window_id}"

    def run_local(self, payload: AgentRunInput) -> JudgeDecision:
        rng = random.Random(payload.rng_seed if payload.rng_seed is not None else 42)
        eligible = [
            r
            for r in payload.council_outputs
            if r.action.value == "buy"
            and r.instrument
            in {
                RecommendationInstrument.EQUITY,
                RecommendationInstrument.ETF,
                RecommendationInstrument.LEVERAGED_ETF,
            }
        ]
        rng.shuffle(eligible)
        chosen = eligible[: min(3, len(eligible))]
        if not chosen:
            return _decision(
                judge_id=self.agent_id,
                track_id=payload.track_id,
                items=(),
                objective_label="random_reference",
                posture=PortfolioPosture.CASH,
                rationale="No eligible candidates; held cash.",
            )
        weight = (Decimal("1") / Decimal(len(chosen))).quantize(Decimal("0.0001"))
        items = tuple(
            ActionItem(
                action_id=f"act_{uuid.uuid4().hex[:16]}",
                track_id=payload.track_id,
                symbol=rec.symbol,
                instrument=rec.instrument,
                side=_side_for(rec.instrument),
                target_notional_usd=None,
                target_pct_equity=weight,
                order_type=ActionOrderType.MARKET,
                limit_price=None,
                stop_price=None,
                time_in_force="day",
                multi_leg=None,
                pair_legs=None,
                rationale=f"Random pick (seed={payload.rng_seed}).",
                contributing_recommendation_ids=(rec.recommendation_id,),
            )
            for rec in chosen
        )
        return _decision(
            judge_id=self.agent_id,
            track_id=payload.track_id,
            items=items,
            objective_label="random_reference",
            posture=PortfolioPosture.NET_LONG,
            rationale=f"Random baseline picked {len(chosen)} candidates equally weighted.",
        )


# ---------------------------------------------------------------------------
# Benchmark — buy-and-hold a configured index ETF.
# ---------------------------------------------------------------------------
DEFAULT_BENCHMARK_SYMBOL = "SPY"


class BenchmarkAgent(BaseAgent[JudgeDecision]):
    agent_id = "benchmark"
    agent_version = "1.0.0"
    prompt_template_id = BENCHMARK_TEMPLATE.template_id
    output_schema = JudgeDecision
    requires_llm = False

    def __init__(self, symbol: str = DEFAULT_BENCHMARK_SYMBOL) -> None:
        self.symbol = symbol

    def render_user_message(self, payload: AgentRunInput) -> str:
        return f"track={payload.track_id} window={payload.window_id} symbol={self.symbol}"

    def run_local(self, payload: AgentRunInput) -> JudgeDecision:
        item = ActionItem(
            action_id=f"act_{uuid.uuid4().hex[:16]}",
            track_id=payload.track_id,
            symbol=self.symbol,
            instrument=RecommendationInstrument.ETF,
            side=OrderSide.BUY,
            target_notional_usd=None,
            target_pct_equity=Decimal("1.0"),
            order_type=ActionOrderType.MARKET,
            limit_price=None,
            stop_price=None,
            time_in_force="day",
            multi_leg=None,
            pair_legs=None,
            rationale=f"Buy-and-hold {self.symbol} 100% benchmark.",
            contributing_recommendation_ids=(),
        )
        return _decision(
            judge_id=self.agent_id,
            track_id=payload.track_id,
            items=(item,),
            objective_label="benchmark_buy_and_hold",
            posture=PortfolioPosture.NET_LONG,
            rationale=f"Benchmark track: 100% {self.symbol} buy-and-hold.",
        )


def make_baselines() -> tuple[BaseAgent[BaseModel], ...]:
    return (
        QuantSignalAgent(),
        RandomBaselineAgent(),
        BenchmarkAgent(),
    )


__all__ = (
    "BENCHMARK_TEMPLATE",
    "DEFAULT_BENCHMARK_SYMBOL",
    "QUANT_SIGNAL_TEMPLATE",
    "RANDOM_BASELINE_TEMPLATE",
    "BenchmarkAgent",
    "QuantSignalAgent",
    "RandomBaselineAgent",
    "make_baselines",
)
