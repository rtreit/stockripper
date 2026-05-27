"""Deterministic regex-based prompt-injection detector.

Phase-3 baseline: catch the well-known injection patterns reliably and
return a strict :class:`PromptInjectionReport`. Phase 4+ may layer an
LLM-corroboration pass on top, but the regex baseline is the part that
must NEVER fail closed silently — it gates whether retrieved content
reaches an agent at all (see :mod:`stockripper.agents.sanitizer`).
"""

from __future__ import annotations

import base64
import binascii
import datetime as dt
import re
import uuid
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from typing import Final

from stockripper.agents.schemas import (
    PromptInjectionFinding,
    PromptInjectionReport,
    Severity,
)

_DETECTOR_AGENT_ID: Final[str] = "prompt_injection_detector"
_DETECTOR_VERSION: Final[str] = "1.0.0"


@dataclass(frozen=True)
class _Pattern:
    pattern_id: str
    regex: re.Pattern[str]
    severity: Severity
    reason: str


_PATTERNS: tuple[_Pattern, ...] = (
    _Pattern(
        "ignore_previous_instructions",
        re.compile(
            r"\b(ignore|disregard|forget)\b[^.\n]{0,40}\b(previous|prior|above|earlier)\b[^.\n]{0,40}\b(instruction|prompt|message|rule)s?\b",
            re.IGNORECASE,
        ),
        Severity.CRITICAL,
        "Classic 'ignore previous instructions' injection.",
    ),
    _Pattern(
        "role_redefinition",
        re.compile(
            r"\byou\s+are\s+now\b|\bact\s+as\b|\bpretend\s+(?:to\s+be|you\s+are)\b|\byour\s+new\s+role\b",
            re.IGNORECASE,
        ),
        Severity.HIGH,
        "Attempts to redefine the agent's role.",
    ),
    _Pattern(
        "system_or_developer_spoof",
        re.compile(
            r"^\s*(?:system|developer|assistant)\s*[:\u2502\u2502\|>\-]",
            re.IGNORECASE | re.MULTILINE,
        ),
        Severity.HIGH,
        "Spoofs a system/developer/assistant role header.",
    ),
    _Pattern(
        "source_container_break",
        re.compile(r"</\s*source\s*>", re.IGNORECASE),
        Severity.CRITICAL,
        "Attempts to close the <source> data-only container.",
    ),
    _Pattern(
        "tool_or_function_call_spoof",
        re.compile(
            r"\btool[_ ]?call\b|\bfunction[_ ]?call\b|\bexecute\s+the\s+following\s+(?:python|bash|shell|code)\b",
            re.IGNORECASE,
        ),
        Severity.HIGH,
        "Looks like a forged tool/function call instruction.",
    ),
    _Pattern(
        "jailbreak_preamble",
        re.compile(
            r"\bDAN\b|\bdo\s+anything\s+now\b|\bdeveloper\s+mode\b|\bjailbreak\b",
            re.IGNORECASE,
        ),
        Severity.HIGH,
        "Known jailbreak preamble.",
    ),
    _Pattern(
        "secret_or_credential_exfiltration",
        re.compile(
            r"\b(?:reveal|share|print|return|email|send|leak|exfil|disclose|dump|expose)\b[^.\n]{0,80}\b(?:api[_ ]?key|secret|password|token|credential|env(?:ironment)?\s+variable)s?\b"
            r"|"
            r"\b(?:api[_ ]?key|secret|password|token|credential|env(?:ironment)?\s+variable)s?\b[^.\n]{0,80}\b(?:reveal|share|print|return|email|send|leak|exfil|disclose|dump|expose)\b",
            re.IGNORECASE,
        ),
        Severity.CRITICAL,
        "Attempts to coerce credential/secret disclosure.",
    ),
    _Pattern(
        "trade_action_override",
        re.compile(
            r"\b(?:place|submit|send)\s+(?:a\s+)?(?:buy|sell|short|long|market|limit)\s+(?:order|trade)\b",
            re.IGNORECASE,
        ),
        Severity.HIGH,
        "Tries to instruct the agent to place a live order.",
    ),
)


_BASE64_BLOCK = re.compile(r"(?<![A-Za-z0-9+/=])[A-Za-z0-9+/]{60,}={0,2}(?![A-Za-z0-9+/=])")
_LIKELY_INJECTION_KEYWORDS = re.compile(
    r"ignore|jailbreak|developer mode|previous instructions|reveal|secret",
    re.IGNORECASE,
)


def _detect_base64_smuggled_instructions(text: str) -> list[tuple[str, str]]:
    """Return ``(snippet, decoded)`` pairs for base64 blocks that decode to
    suspicious instruction text.
    """

    hits: list[tuple[str, str]] = []
    for match in _BASE64_BLOCK.finditer(text):
        block = match.group(0)
        try:
            decoded = base64.b64decode(block, validate=True).decode("utf-8", "ignore")
        except (binascii.Error, ValueError):
            continue
        if _LIKELY_INJECTION_KEYWORDS.search(decoded):
            hits.append((block[:120], decoded[:200]))
    return hits


def _now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def detect_findings(
    text: str, *, evidence_id: str | None = None, now: dt.datetime | None = None,
) -> list[PromptInjectionFinding]:
    """Return every prompt-injection finding present in ``text``."""

    detected_at = now if now is not None else _now()
    out: list[PromptInjectionFinding] = []
    for pat in _PATTERNS:
        match = pat.regex.search(text)
        if match is None:
            continue
        snippet = text[max(0, match.start() - 30): match.end() + 30]
        out.append(
            PromptInjectionFinding(
                pattern_id=pat.pattern_id,
                severity=pat.severity,
                snippet=snippet[:400],
                reason=pat.reason,
                detected_at=detected_at,
                evidence_id=evidence_id,
            )
        )
    for snippet, decoded in _detect_base64_smuggled_instructions(text):
        out.append(
            PromptInjectionFinding(
                pattern_id="base64_smuggled_instructions",
                severity=Severity.HIGH,
                snippet=f"base64:{snippet} -> {decoded}"[:400],
                reason="Base64-decoded text contains likely injection keywords.",
                detected_at=detected_at,
                evidence_id=evidence_id,
            )
        )
    return out


def scan_evidence(
    items: Sequence[tuple[str, str]],
    *,
    track_id: str = "shared",
    now: dt.datetime | None = None,
) -> PromptInjectionReport:
    """Scan a sequence of ``(evidence_id, sanitized_text)`` tuples.

    The text MUST already be sanitized — see
    :func:`stockripper.agents.sanitizer.sanitize_content`.
    """

    detected_at = now if now is not None else _now()
    findings: list[PromptInjectionFinding] = []
    scanned_ids: list[str] = []
    for evidence_id, text in items:
        scanned_ids.append(evidence_id)
        findings.extend(detect_findings(text, evidence_id=evidence_id, now=detected_at))
    return PromptInjectionReport(
        report_id=f"pi_{uuid.uuid4().hex[:16]}",
        agent_id=_DETECTOR_AGENT_ID,
        agent_version=_DETECTOR_VERSION,
        findings=tuple(findings),
        scanned_evidence_ids=tuple(scanned_ids),
        created_at=detected_at,
    )


def all_pattern_ids() -> tuple[str, ...]:
    """Used by tests to confirm every shipped pattern is exercised."""

    return (*tuple(p.pattern_id for p in _PATTERNS), "base64_smuggled_instructions")


def supported_patterns() -> Iterable[tuple[str, Severity, str]]:
    for p in _PATTERNS:
        yield p.pattern_id, p.severity, p.reason


__all__ = (
    "all_pattern_ids",
    "detect_findings",
    "scan_evidence",
    "supported_patterns",
)
