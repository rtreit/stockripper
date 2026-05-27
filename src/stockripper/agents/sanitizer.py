"""Pre-LLM content sanitization.

Every external string that ever reaches a council/judge prompt MUST pass
through :func:`sanitize_content` first. The contract is:

1. Strip HTML tags, ``<script>``, ``<style>``, and other executable
   structure.
2. Normalize Unicode to NFKC and remove zero-width / bidi-override
   characters that fool both humans and tokenizers.
3. Bound output length so a single retrieved page can't blow the prompt.
4. Return the sanitized text alongside a list of issues encountered,
   which the prompt-injection detector consumes.

This module also exposes :func:`wrap_source_container` which puts the
sanitized text inside a ``<source id="...">...</source>`` envelope.
Council/judge prompt cores instruct the model to treat anything between
``<source>`` tags as data, never as instructions. The wrapping happens
*after* sanitization so injection attempts that try to close a source
tag have already had their angle brackets escaped.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from typing import Final

# Characters that vanish visually but change tokenization (zero-width
# joiner, RTL override, soft hyphen, etc.). Listed by codepoint so the
# source file itself doesn't carry the very characters it strips.
_ZERO_WIDTH_AND_BIDI: Final[tuple[str, ...]] = (
    "\u200b",  # ZWSP
    "\u200c",  # ZWNJ
    "\u200d",  # ZWJ
    "\u2060",  # WORD JOINER
    "\ufeff",  # ZERO WIDTH NO-BREAK SPACE (BOM)
    "\u00ad",  # SOFT HYPHEN
    "\u202a", "\u202b", "\u202c", "\u202d", "\u202e",  # LRE/RLE/PDF/LRO/RLO
    "\u2066", "\u2067", "\u2068", "\u2069",            # LRI/RLI/FSI/PDI
)

_STRIP_TAGS = re.compile(
    r"<(script|style|iframe|noscript|template)[^>]*>.*?</\1>",
    re.IGNORECASE | re.DOTALL,
)
_ANY_TAG = re.compile(r"<[^>]+>")
_DEFAULT_MAX_LEN: Final[int] = 8192


@dataclass(frozen=True)
class SanitizationResult:
    """Output of :func:`sanitize_content`."""

    sanitized: str
    stripped_tag_count: int
    removed_hidden_char_count: int
    truncated: bool


def sanitize_content(raw: str, *, max_length: int = _DEFAULT_MAX_LEN) -> SanitizationResult:
    """Strip + normalize a raw retrieved string before any LLM sees it."""

    if not isinstance(raw, str):
        raise TypeError("sanitize_content requires a str")

    body = _STRIP_TAGS.sub(" ", raw)
    stripped_tag_count = len(_ANY_TAG.findall(body))
    body = _ANY_TAG.sub(" ", body)

    body = unicodedata.normalize("NFKC", body)
    hidden_removed = 0
    for ch in _ZERO_WIDTH_AND_BIDI:
        if ch in body:
            hidden_removed += body.count(ch)
            body = body.replace(ch, "")

    # Collapse runs of whitespace so the prompt stays compact.
    body = re.sub(r"\s+", " ", body).strip()

    truncated = False
    if len(body) > max_length:
        body = body[:max_length]
        truncated = True
    return SanitizationResult(
        sanitized=body,
        stripped_tag_count=stripped_tag_count,
        removed_hidden_char_count=hidden_removed,
        truncated=truncated,
    )


def wrap_source_container(text: str, *, source_id: str) -> str:
    """Wrap sanitized text in a structural source container.

    The text is XML-escaped first so a malicious source cannot close the
    container prematurely.
    """

    escaped = (
        text.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )
    sid = "".join(c for c in source_id if c.isalnum() or c in "_-:") or "src"
    return f'<source id="{sid}">{escaped}</source>'


__all__ = (
    "SanitizationResult",
    "sanitize_content",
    "wrap_source_container",
)
