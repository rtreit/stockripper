"""Tests for the Provenance pydantic model."""

from __future__ import annotations

import json

import pytest
from pydantic import ValidationError

from stockripper.data.provenance import Provenance


def test_for_payload_is_deterministic_for_dicts() -> None:
    a = Provenance.for_payload(
        provider="sec_edgar",
        source_url="https://data.sec.gov/api/xbrl/companyfacts/CIK0000320193.json",
        payload={"b": 2, "a": 1},
    )
    b = Provenance.for_payload(
        provider="sec_edgar",
        source_url="https://data.sec.gov/api/xbrl/companyfacts/CIK0000320193.json",
        payload={"a": 1, "b": 2},  # different key order, same content
    )
    assert a.content_hash == b.content_hash
    assert len(a.content_hash) == 64


def test_for_payload_handles_bytes_and_strings() -> None:
    raw = b'{"hi":1}'
    a = Provenance.for_payload(provider="x", source_url="x://1", payload=raw)
    b = Provenance.for_payload(provider="x", source_url="x://1", payload=raw.decode("utf-8"))
    # bytes hashed directly; string hashed as utf-8 bytes of the literal,
    # which equals the byte content, so both should match.
    assert a.content_hash == b.content_hash


def test_provenance_is_frozen() -> None:
    p = Provenance.for_payload(provider="x", source_url="x://1", payload={"a": 1})
    with pytest.raises((ValidationError, TypeError)):
        p.provider = "other"


def test_extra_fields_are_rejected() -> None:
    p = Provenance.for_payload(provider="x", source_url="x://1", payload={"a": 1})
    raw = p.model_dump_json()
    payload = json.loads(raw)
    payload["unknown"] = "field"
    with pytest.raises(ValidationError):
        Provenance.model_validate(payload)
