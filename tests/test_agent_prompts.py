"""Prompt registry & content-hash versioning tests."""

from __future__ import annotations

import pytest

from stockripper.agents.prompts import (
    AGGRESSIVE_JUDGE_CORE,
    BALANCED_JUDGE_CORE,
    CONCENTRATED_JUDGE_CORE,
    CONSERVATIVE_JUDGE_CORE,
    PROMPTS,
    RISK_MANAGER_CORE,
    SKEPTIC_CORE,
    UNIVERSAL_POLICY_PREAMBLE,
    YOLO_JUDGE_CORE,
    PromptRegistry,
    PromptTemplate,
)


def test_universal_policy_preamble_present_in_rendered_body() -> None:
    rendered = SKEPTIC_CORE.render()
    assert UNIVERSAL_POLICY_PREAMBLE in rendered


def test_universal_policy_preamble_can_be_omitted() -> None:
    body_only = SKEPTIC_CORE.render(include_preamble=False)
    assert UNIVERSAL_POLICY_PREAMBLE not in body_only


def test_content_hash_is_deterministic_and_unique_per_body() -> None:
    seen = {
        SKEPTIC_CORE.content_hash,
        RISK_MANAGER_CORE.content_hash,
        YOLO_JUDGE_CORE.content_hash,
        CONSERVATIVE_JUDGE_CORE.content_hash,
        BALANCED_JUDGE_CORE.content_hash,
        AGGRESSIVE_JUDGE_CORE.content_hash,
        CONCENTRATED_JUDGE_CORE.content_hash,
    }
    # Every template should hash to a distinct value (no copy-paste cores).
    assert len(seen) == 7


def test_rendered_content_hash_differs_from_body_only_hash() -> None:
    assert SKEPTIC_CORE.content_hash != SKEPTIC_CORE.rendered_content_hash


def test_registry_rejects_duplicate_id_with_different_body() -> None:
    registry = PromptRegistry()
    registry.register(PromptTemplate(template_id="t.x", version="1", body="hello"))
    with pytest.raises(ValueError):
        registry.register(PromptTemplate(template_id="t.x", version="2", body="hello world"))


def test_registry_idempotent_on_exact_re_register() -> None:
    registry = PromptRegistry()
    a = registry.register(PromptTemplate(template_id="t.y", version="1", body="hi"))
    b = registry.register(PromptTemplate(template_id="t.y", version="1", body="hi"))
    assert a is b or a.content_hash == b.content_hash


def test_global_registry_contains_all_council_and_judge_templates() -> None:
    # Ensure council templates are registered (registration happens at
    # CouncilAgent construction).
    from stockripper.agents.council import make_council

    make_council()

    expected_subset = {
        "adversarial.skeptic",
        "adversarial.risk_manager",
        "judge.yolo",
        "judge.conservative",
        "judge.balanced",
        "judge.aggressive",
        "judge.concentrated",
        "council.quality",
        "council.value",
        "council.market_climate",
    }
    ids = {t.template_id for t in PROMPTS.all_templates()}
    assert expected_subset.issubset(ids), expected_subset - ids
