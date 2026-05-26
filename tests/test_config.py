"""Tests for the paper-endpoint fail-closed config invariant.

These tests encode universal floor #1 from ``PROJECT_SPEC.md`` §16.1: the
application must refuse to start against any non-paper Alpaca endpoint.
Removing or weakening any test in this module is a security-policy change
and requires a corresponding spec amendment.
"""

from __future__ import annotations

import pytest
from pydantic import SecretStr

from stockripper.config import (
    PAPER_HOSTS,
    PaperEndpointError,
    StockripperSettings,
    load_settings,
    redact_secrets,
)


def test_paper_hosts_contains_only_paper_endpoint() -> None:
    assert frozenset({"paper-api.alpaca.markets"}) == PAPER_HOSTS
    assert "api.alpaca.markets" not in PAPER_HOSTS


def test_load_settings_succeeds_with_paper_endpoint(paper_env: None) -> None:
    settings = load_settings()
    settings.assert_paper_only()
    assert settings.alpaca_base_url.startswith("https://paper-api.alpaca.markets")
    assert settings.openai_model_default  # default applied


@pytest.mark.parametrize(
    "bad_url",
    [
        "https://api.alpaca.markets/v2",
        "https://api.alpaca.markets",
        "http://paper-api.alpaca.markets/v2",  # wrong scheme
        "https://evil.example.com/v2",
        "https://Api.Alpaca.Markets/v2",  # case-only difference, still live host
    ],
)
def test_load_settings_refuses_non_paper_endpoint(
    paper_env: None, monkeypatch: pytest.MonkeyPatch, bad_url: str
) -> None:
    monkeypatch.setenv("ALPACA_BASE_URL", bad_url)
    with pytest.raises(Exception) as excinfo:
        load_settings()
    # pydantic-settings wraps validator errors in a ValidationError; either way
    # the root cause must be our PaperEndpointError so the operator sees why.
    rendered = str(excinfo.value).lower()
    assert "paper" in rendered or isinstance(excinfo.value.__cause__, PaperEndpointError)


def test_load_settings_rejects_missing_credentials() -> None:
    with pytest.raises(Exception) as excinfo:
        load_settings()
    msg = str(excinfo.value)
    assert "ALPACA_API_KEY_ID" in msg or "alpaca_api_key_id" in msg.lower()


def test_assert_paper_only_is_defensively_callable(paper_env: None) -> None:
    settings = load_settings()
    settings.assert_paper_only()
    # Mutating the field and re-checking must raise — execution-adapter code
    # paths re-check this invariant at every call site.
    object.__setattr__(settings, "alpaca_base_url", "https://api.alpaca.markets/v2")
    with pytest.raises(PaperEndpointError):
        settings.assert_paper_only()


def test_redact_secrets_never_leaks_raw_values(paper_env: None) -> None:
    settings = load_settings()
    redacted = redact_secrets(settings)
    raw_key = settings.alpaca_api_key_id.get_secret_value()
    raw_secret = settings.alpaca_api_secret_key.get_secret_value()
    raw_openai = settings.openai_api_key.get_secret_value()
    rendered = "\n".join(redacted.values())
    assert raw_key not in rendered
    assert raw_secret not in rendered
    assert raw_openai not in rendered
    # The paper URL is fine to log.
    assert redacted["alpaca_base_url"] == settings.alpaca_base_url


def test_database_url_password_is_redacted_for_logging() -> None:
    settings = StockripperSettings(
        ALPACA_API_KEY_ID=SecretStr("PKTEST0000000000TEST"),
        ALPACA_API_SECRET_KEY=SecretStr("test-secret"),
        OPENAI_API_KEY=SecretStr("sk-test"),
        DATABASE_URL=(
            "postgresql+psycopg://stockripper:supersecretpw@db.local:5432/stockripper"
        ),
    )
    redacted = redact_secrets(settings)
    assert "supersecretpw" not in redacted["database_url"]
    assert "stockripper" in redacted["database_url"]
