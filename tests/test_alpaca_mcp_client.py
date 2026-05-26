"""Tests for the alpaca MCP client adapter.

These tests spawn the *real* alpaca-mcp server as a subprocess (stdio
transport) and assert on the tool list. They don't hit any Alpaca endpoint —
``list_tools`` is purely an MCP handshake — so they're safe to run offline.
"""

from __future__ import annotations

import shutil

import pytest

from stockripper.agents import (
    ALPACA_MCP_DIRECTORY,
    AlpacaMcpClient,
    build_alpaca_mcp_env,
)

EXPECTED_TOOLS: frozenset[str] = frozenset({
    "get_account", "get_clock", "get_calendar",
    "get_positions", "get_position", "close_position", "close_all_positions",
    "get_orders", "get_order", "submit_order", "replace_order",
    "cancel_order", "cancel_all_orders",
    "get_portfolio_history", "get_assets", "get_asset",
    "get_stock_bars", "get_stock_latest_quote", "get_stock_latest_trade",
    "get_stock_snapshot",
    "get_news",
    "get_option_contracts", "get_option_chain", "get_option_snapshot",
    "get_option_latest_quote", "get_option_bars",
    "alpaca_mode",
})


# ----------------------------------------------------------------------
# Env-build tests (no subprocess, fast)
# ----------------------------------------------------------------------
def test_build_env_forces_paper_and_injects_creds() -> None:
    env = build_alpaca_mcp_env(
        "PK-test-key",
        "secret-value",
        base_env={"PATH": "/usr/bin", "FOO": "bar"},
    )

    assert env["ALPACA_MODE"] == "paper"
    assert env["ALPACA_API_KEY_ID"] == "PK-test-key"
    assert env["ALPACA_API_SECRET_KEY"] == "secret-value"
    assert env["PATH"] == "/usr/bin"
    assert env["FOO"] == "bar"


def test_build_env_strips_live_knobs_from_base() -> None:
    env = build_alpaca_mcp_env(
        "PK-x",
        "sec",
        base_env={
            "ALPACA_MODE": "live",
            "ALPACA_ALLOW_LIVE": "true",
            "OTHER": "kept",
        },
    )

    assert env["ALPACA_MODE"] == "paper", "live mode must be overridden"
    assert "ALPACA_ALLOW_LIVE" not in env, "live escape hatch must be stripped"
    assert env["OTHER"] == "kept"


def test_build_env_uses_real_os_environ_when_base_is_none(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("ALPACA_MODE", "live")
    monkeypatch.setenv("ALPACA_ALLOW_LIVE", "true")
    monkeypatch.setenv("CARRIED_THROUGH", "yes")

    env = build_alpaca_mcp_env("k", "s")

    assert env["ALPACA_MODE"] == "paper"
    assert "ALPACA_ALLOW_LIVE" not in env
    assert env["CARRIED_THROUGH"] == "yes"


def test_alpaca_mcp_directory_resolves_to_existing_path() -> None:
    assert ALPACA_MCP_DIRECTORY.exists(), (
        f"expected the alpaca_mcp project at {ALPACA_MCP_DIRECTORY}"
    )
    assert (ALPACA_MCP_DIRECTORY / "pyproject.toml").is_file()


# ----------------------------------------------------------------------
# Live subprocess test (spawns the real MCP server via uv)
# ----------------------------------------------------------------------
_HAS_UV = shutil.which("uv") is not None


@pytest.mark.skipif(not _HAS_UV, reason="uv binary not on PATH")
async def test_spawn_lists_all_expected_tools() -> None:
    async with AlpacaMcpClient.spawn(
        api_key_id="PK-test-handshake-only",
        api_secret_key="not-a-real-secret",
    ) as client:
        names = set(await client.tool_names())

    assert names >= EXPECTED_TOOLS, (
        f"missing tools: {EXPECTED_TOOLS - names}"
    )
