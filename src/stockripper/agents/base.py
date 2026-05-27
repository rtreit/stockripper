"""Base agent + boilerplate shared by every Phase-3 council/judge/adversary.

A Phase-3 agent is a small object that:

1. Renders a prompt from its template + an :class:`AgentRunInput`.
2. Calls the structured LLM client (or a deterministic local planner).
3. Parses output into a strict pydantic model.
4. Returns an :class:`AgentRunResult` whose ``status`` is either OK or
   QUARANTINED. **Agents never raise from ``run``.**
"""

from __future__ import annotations

import datetime as dt
import hashlib
import json
import logging
from abc import ABC, abstractmethod
from decimal import Decimal
from typing import cast

from pydantic import BaseModel, ValidationError

from stockripper.agents.ids import agent_run_id as _derive_agent_run_id
from stockripper.agents.llm import (
    LLMClient,
    StructuredResponse,
    schema_content_hash,
)
from stockripper.agents.prompts import PROMPTS, PromptTemplate
from stockripper.agents.schemas import (
    AgentOutput,
    AgentRunInput,
    AgentRunResult,
    AgentRunStatus,
    RequestFingerprint,
)

LOG = logging.getLogger("stockripper.agents")


def _now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def _hash_str(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def serialize_input(payload: AgentRunInput) -> tuple[str, str]:
    """Return ``(canonical_json, sha256_hex)`` for an :class:`AgentRunInput`."""

    text = json.dumps(payload.model_dump(mode="json"), sort_keys=True, default=str)
    return text, _hash_str(text)


_QUARANTINE_HASH = "0" * 64


def _quarantine_fingerprint(*, model_id: str, input_hash: str) -> RequestFingerprint:
    return RequestFingerprint(
        model_id=model_id,
        temperature=Decimal("0"),
        top_p=Decimal("1"),
        prompt_content_hash=_QUARANTINE_HASH,
        schema_content_hash=_QUARANTINE_HASH,
        input_content_hash=input_hash,
        seed=None,
    )


class BaseAgent[TOutput: BaseModel](ABC):
    """Base class for every Phase 3 agent."""

    agent_id: str
    agent_version: str
    prompt_template_id: str
    output_schema: type[BaseModel]
    model_id_override: str | None = None
    requires_llm: bool = True

    @property
    def template(self) -> PromptTemplate:
        return PROMPTS.get(self.prompt_template_id)

    @abstractmethod
    def render_user_message(self, payload: AgentRunInput) -> str: ...

    def run_local(self, payload: AgentRunInput) -> TOutput:  # pragma: no cover
        raise NotImplementedError(
            f"{self.agent_id}: requires_llm=False but run_local not implemented"
        )

    # ------------------------------------------------------------------
    def run(
        self,
        payload: AgentRunInput,
        *,
        llm: LLMClient | None = None,
        seed: int | None = None,
        now: dt.datetime | None = None,
    ) -> AgentRunResult:
        created_at = now if now is not None else _now()
        template = self.template
        _input_text, input_hash = serialize_input(payload)
        # Deterministic AgentRunResult.run_id derived from stable inputs
        # so replay can address agent runs idempotently.
        run_id = _derive_agent_run_id(
            track_run_id=payload.run_id,
            agent_id=self.agent_id,
            input_hash=input_hash,
        )
        model_id_for_quarantine = self.model_id_override or "unknown"

        try:
            user_message = self.render_user_message(payload)
        except Exception as exc:  # agents never raise.
            LOG.exception("Agent %s failed to render user message", self.agent_id)
            return self._quarantine(
                run_id=run_id,
                payload=payload,
                created_at=created_at,
                reason=f"render_user_message raised: {exc!r}",
                fingerprint=_quarantine_fingerprint(
                    model_id=model_id_for_quarantine, input_hash=input_hash
                ),
            )

        full_prompt = (
            f"{template.render()}\n\n# Task input\n{user_message}"
        )
        prompt_hash = _hash_str(full_prompt)
        sch_hash = schema_content_hash(self.output_schema)
        effective_seed = seed if seed is not None else payload.rng_seed

        # ---- non-LLM path (baselines, deterministic agents) ------------
        if not self.requires_llm or llm is None:
            if self.requires_llm and llm is None:
                return self._quarantine(
                    run_id=run_id,
                    payload=payload,
                    created_at=created_at,
                    reason="LLM client required but not supplied",
                    fingerprint=_quarantine_fingerprint(
                        model_id=model_id_for_quarantine, input_hash=input_hash
                    ),
                )
            try:
                parsed_local = self.run_local(payload)
            except (ValidationError, ValueError, RuntimeError, KeyError) as exc:
                return self._quarantine(
                    run_id=run_id,
                    payload=payload,
                    created_at=created_at,
                    reason=f"run_local raised: {exc!r}",
                    fingerprint=_quarantine_fingerprint(
                        model_id=model_id_for_quarantine, input_hash=input_hash
                    ),
                )
            fingerprint = RequestFingerprint(
                model_id=self.model_id_override or "deterministic",
                temperature=Decimal("0"),
                top_p=Decimal("1"),
                prompt_content_hash=prompt_hash,
                schema_content_hash=sch_hash,
                input_content_hash=input_hash,
                seed=effective_seed,
            )
            return AgentRunResult(
                run_id=run_id,
                agent_id=self.agent_id,
                agent_version=self.agent_version,
                track_id=payload.track_id,
                status=AgentRunStatus.OK,
                fingerprint=fingerprint,
                output=cast(AgentOutput, parsed_local),
                raw_response_text=json.dumps(parsed_local.model_dump(mode="json"), default=str),
                quarantine_reason=None,
                latency_ms=0,
                created_at=created_at,
            )

        # ---- LLM path ----------------------------------------------------
        try:
            response: StructuredResponse = llm.run_structured(
                prompt=full_prompt,
                schema=self.output_schema,
                agent_id=self.agent_id,
                model_id=self.model_id_override,
                seed=effective_seed,
                temperature=0.0,
                top_p=1.0,
                prompt_content_hash=prompt_hash,
                schema_content_hash=sch_hash,
                input_content_hash=input_hash,
            )
        except (ValueError, KeyError, RuntimeError) as exc:
            LOG.warning("Agent %s LLM call failed: %r", self.agent_id, exc)
            return self._quarantine(
                run_id=run_id,
                payload=payload,
                created_at=created_at,
                reason=f"LLM call failed: {exc!r}",
                fingerprint=_quarantine_fingerprint(
                    model_id=model_id_for_quarantine, input_hash=input_hash
                ),
            )

        parsed = response.parsed
        if not isinstance(parsed, self.output_schema):
            try:
                parsed = self.output_schema.model_validate(parsed.model_dump())
            except ValidationError as exc:
                return self._quarantine(
                    run_id=run_id,
                    payload=payload,
                    created_at=created_at,
                    reason=f"output schema mismatch: {exc!r}",
                    raw_text=response.raw_text,
                    fingerprint=_quarantine_fingerprint(
                        model_id=response.model_id, input_hash=input_hash
                    ),
                )

        fingerprint = RequestFingerprint(
            model_id=response.model_id,
            temperature=Decimal("0"),
            top_p=Decimal("1"),
            prompt_content_hash=prompt_hash,
            schema_content_hash=sch_hash,
            input_content_hash=input_hash,
            seed=effective_seed,
        )

        return AgentRunResult(
            run_id=run_id,
            agent_id=self.agent_id,
            agent_version=self.agent_version,
            track_id=payload.track_id,
            status=AgentRunStatus.OK,
            fingerprint=fingerprint,
            output=cast(AgentOutput, parsed),
            raw_response_text=response.raw_text,
            quarantine_reason=None,
            latency_ms=response.latency_ms,
            created_at=created_at,
        )

    # ------------------------------------------------------------------
    def _quarantine(
        self,
        *,
        run_id: str,
        payload: AgentRunInput,
        created_at: dt.datetime,
        reason: str,
        fingerprint: RequestFingerprint,
        raw_text: str | None = None,
    ) -> AgentRunResult:
        return AgentRunResult(
            run_id=run_id,
            agent_id=self.agent_id,
            agent_version=self.agent_version,
            track_id=payload.track_id,
            status=AgentRunStatus.QUARANTINED,
            fingerprint=fingerprint,
            output=None,
            raw_response_text=raw_text or "",
            quarantine_reason=reason,
            latency_ms=0,
            created_at=created_at,
        )


ParsedAgentOutput = AgentOutput


__all__ = ("BaseAgent", "ParsedAgentOutput", "serialize_input")
