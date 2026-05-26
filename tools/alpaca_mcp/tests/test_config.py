"""Tests for the live-trading double-confirmation safety gate.

These tests encode the only safety contract this MCP server makes: switching
to live mode requires *both* ``ALPACA_MODE=live`` and ``ALPACA_ALLOW_LIVE=true``
to be set in the same environment. Weakening these tests is a security
policy change.
"""

from __future__ import annotations

import pytest

from alpaca_mcp.config import (
    LIVE_TRADING_URL,
    PAPER_TRADING_URL,
    AlpacaMcpSettings,
    AlpacaMode,
    LiveTradingGateError,
    load_settings,
)


def test_defaults_to_paper(paper_creds: None) -> None:
    settings = load_settings()
    assert settings.alpaca_mode is AlpacaMode.PAPER
    assert settings.is_paper is True
    assert settings.trading_base_url == PAPER_TRADING_URL


def test_explicit_paper_mode(paper_creds: None, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ALPACA_MODE", "paper")
    settings = load_settings()
    assert settings.alpaca_mode is AlpacaMode.PAPER


def test_live_without_allow_live_is_refused(
    paper_creds: None, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("ALPACA_MODE", "live")
    with pytest.raises(Exception) as excinfo:
        load_settings()
    msg = str(excinfo.value)
    assert "ALPACA_ALLOW_LIVE" in msg or isinstance(excinfo.value.__cause__, LiveTradingGateError)


def test_live_with_only_allow_live_stays_paper(
    paper_creds: None, monkeypatch: pytest.MonkeyPatch
) -> None:
    # Setting ALPACA_ALLOW_LIVE=true alone (without ALPACA_MODE=live) must
    # still leave the server in paper mode. Both knobs are required.
    monkeypatch.setenv("ALPACA_ALLOW_LIVE", "true")
    settings = load_settings()
    assert settings.is_paper is True


def test_live_with_both_armed_is_permitted(live_armed: None) -> None:
    settings = load_settings()
    assert settings.alpaca_mode is AlpacaMode.LIVE
    assert settings.is_paper is False
    assert settings.trading_base_url == LIVE_TRADING_URL


def test_missing_credentials_rejected() -> None:
    with pytest.raises(Exception) as excinfo:
        load_settings()
    msg = str(excinfo.value)
    assert "ALPACA_API_KEY_ID" in msg or "alpaca_api_key_id" in msg.lower()


def test_paper_banner_does_not_shout(paper_creds: None) -> None:
    settings = load_settings()
    banner = settings.banner()
    assert "PAPER" in banner
    assert "REAL MONEY" not in banner


def test_live_banner_shouts(live_armed: None) -> None:
    settings = load_settings()
    banner = settings.banner()
    assert "LIVE" in banner
    assert "REAL MONEY" in banner


def test_settings_repr_does_not_leak_secrets(paper_creds: None) -> None:
    settings = load_settings()
    rendered = repr(settings)
    assert "test-secret" not in rendered


def test_construct_directly_with_only_paper_credentials() -> None:
    from pydantic import SecretStr

    settings = AlpacaMcpSettings(
        ALPACA_API_KEY_ID=SecretStr("PKTEST"),
        ALPACA_API_SECRET_KEY=SecretStr("s"),
    )
    assert settings.is_paper is True
