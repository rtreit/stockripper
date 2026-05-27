"""Structured-output LLM client wrapper.

Phase 3 agents call exactly one LLM through this surface. Two
implementations:

* :class:`OpenAIStructuredClient` — production wrapper around the
  OpenAI Responses API with strict JSON-schema enforcement via
  ``response_format``. Reads the API key + default model from
  :func:`stockripper.config.load_settings`.
* :class:`FakeLLMClient` — deterministic, no network. Tests stamp
  canned :class:`StructuredResponse` instances keyed by agent_id or
  fingerprint digest so council/judge logic is fully testable without
  hitting OpenAI.

The :class:`LLMClient` :class:`Protocol` is what every Phase-3 agent
parameter is typed against; production and test wiring are
interchangeable.
"""

from __future__ import annotations

import contextlib
import datetime as dt
import hashlib
import json
import time
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Any, Final, Protocol, TypeVar, runtime_checkable

from pydantic import BaseModel, ValidationError

T = TypeVar("T", bound=BaseModel)


@dataclass(frozen=True)
class StructuredResponse:
    """Outcome of one structured LLM call (parsed + raw)."""

    parsed: BaseModel
    raw_text: str
    model_id: str
    latency_ms: int
    finish_reason: str
    request_fingerprint_digest: str


@runtime_checkable
class LLMClient(Protocol):
    """Minimal structured-output surface every Phase-3 agent depends on."""

    def run_structured(
        self,
        *,
        prompt: str,
        schema: type[T],
        agent_id: str,
        model_id: str | None = None,
        seed: int | None = None,
        temperature: float = 0.0,
        top_p: float = 1.0,
        prompt_content_hash: str,
        schema_content_hash: str,
        input_content_hash: str,
    ) -> StructuredResponse: ...


def _composite_digest(
    *,
    model_id: str,
    temperature: float,
    top_p: float,
    seed: int | None,
    prompt_content_hash: str,
    schema_content_hash: str,
    input_content_hash: str,
) -> str:
    h = hashlib.sha256()
    parts = (
        model_id,
        f"{temperature:.6f}",
        f"{top_p:.6f}",
        str(seed if seed is not None else "none"),
        prompt_content_hash,
        schema_content_hash,
        input_content_hash,
    )
    h.update("\n".join(parts).encode("utf-8"))
    return h.hexdigest()


def schema_content_hash(schema: type[BaseModel]) -> str:
    """Stable hash of a pydantic schema's JSON-schema representation."""

    payload = json.dumps(schema.model_json_schema(), sort_keys=True, default=str)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------------------
# OpenAI implementation (lazy import so tests don't require openai online).
# ---------------------------------------------------------------------------


_DEFAULT_TEMPERATURE: Final[float] = 0.0


class OpenAIStructuredClient:
    """Production implementation. Uses the structured-output API."""

    def __init__(
        self,
        *,
        api_key: str,
        default_model: str,
        timeout_s: float = 60.0,
    ) -> None:
        try:
            from openai import OpenAI
        except ImportError as exc:  # pragma: no cover - dep is declared
            raise RuntimeError("openai package not installed") from exc
        self._client: Any = OpenAI(api_key=api_key, timeout=timeout_s)
        self._default_model = default_model

    def run_structured(
        self,
        *,
        prompt: str,
        schema: type[T],
        agent_id: str,
        model_id: str | None = None,
        seed: int | None = None,
        temperature: float = _DEFAULT_TEMPERATURE,
        top_p: float = 1.0,
        prompt_content_hash: str,
        schema_content_hash: str,
        input_content_hash: str,
    ) -> StructuredResponse:
        chosen_model = model_id or self._default_model
        digest = _composite_digest(
            model_id=chosen_model,
            temperature=temperature,
            top_p=top_p,
            seed=seed,
            prompt_content_hash=prompt_content_hash,
            schema_content_hash=schema_content_hash,
            input_content_hash=input_content_hash,
        )
        started = time.perf_counter()
        response: Any = self._client.responses.parse(
            model=chosen_model,
            input=prompt,
            text_format=schema,
            temperature=temperature,
            top_p=top_p,
            **({"seed": seed} if seed is not None else {}),
            metadata={"agent_id": agent_id, "fingerprint": digest[:32]},
        )
        latency_ms = int((time.perf_counter() - started) * 1000)
        parsed = response.output_parsed
        if parsed is None:
            raise ValueError(
                f"OpenAI returned no parsed output for agent_id={agent_id!r}"
            )
        raw_text = getattr(response, "output_text", None) or json.dumps(
            parsed.model_dump(mode="json"), default=str
        )
        finish = "stop"
        with contextlib.suppress(AttributeError, IndexError):
            finish = response.output[0].content[0].finish_reason or "stop"
        return StructuredResponse(
            parsed=parsed,
            raw_text=raw_text,
            model_id=chosen_model,
            latency_ms=latency_ms,
            finish_reason=finish,
            request_fingerprint_digest=digest,
        )


# ---------------------------------------------------------------------------
# Fake implementation
# ---------------------------------------------------------------------------


class FakeLLMClient:
    """Test client. Stamps canned responses.

    Lookup priority:
        1. exact fingerprint digest
        2. agent_id
        3. ``"*"`` (default)

    Each canned entry is a tuple of ``(model: BaseModel, raw_text: str)``.
    The model is validated against the schema the agent passes in so a
    miswired test fails loudly.
    """

    def __init__(
        self,
        canned: Mapping[str, tuple[BaseModel, str]] | None = None,
        *,
        default_model_id: str = "fake-model",
        fixed_latency_ms: int = 1,
    ) -> None:
        self._canned: dict[str, tuple[BaseModel, str]] = dict(canned or {})
        self._default_model_id = default_model_id
        self._fixed_latency_ms = fixed_latency_ms
        self.calls: list[Mapping[str, Any]] = []

    def install(self, key: str, model: BaseModel, raw_text: str | None = None) -> None:
        self._canned[key] = (model, raw_text or json.dumps(model.model_dump(mode="json"), default=str))

    def run_structured(
        self,
        *,
        prompt: str,
        schema: type[T],
        agent_id: str,
        model_id: str | None = None,
        seed: int | None = None,
        temperature: float = _DEFAULT_TEMPERATURE,
        top_p: float = 1.0,
        prompt_content_hash: str,
        schema_content_hash: str,
        input_content_hash: str,
    ) -> StructuredResponse:
        chosen_model = model_id or self._default_model_id
        digest = _composite_digest(
            model_id=chosen_model,
            temperature=temperature,
            top_p=top_p,
            seed=seed,
            prompt_content_hash=prompt_content_hash,
            schema_content_hash=schema_content_hash,
            input_content_hash=input_content_hash,
        )
        entry = (
            self._canned.get(digest)
            or self._canned.get(agent_id)
            or self._canned.get("*")
        )
        if entry is None:
            raise KeyError(
                f"FakeLLMClient: no canned response for agent_id={agent_id!r} "
                f"digest={digest[:16]}"
            )
        model_obj, raw_text = entry
        if not isinstance(model_obj, schema):
            try:
                model_obj = schema.model_validate(model_obj.model_dump())
            except ValidationError as exc:
                raise TypeError(
                    f"FakeLLMClient: canned response for {agent_id!r} is not a "
                    f"{schema.__name__}"
                ) from exc
        self.calls.append(
            {
                "agent_id": agent_id,
                "digest": digest,
                "prompt_length": len(prompt),
                "model_id": chosen_model,
                "seed": seed,
                "at": dt.datetime.now(dt.UTC),
            }
        )
        return StructuredResponse(
            parsed=model_obj,
            raw_text=raw_text,
            model_id=chosen_model,
            latency_ms=self._fixed_latency_ms,
            finish_reason="stop",
            request_fingerprint_digest=digest,
        )


__all__ = (
    "FakeLLMClient",
    "LLMClient",
    "OpenAIStructuredClient",
    "StructuredResponse",
    "schema_content_hash",
)
