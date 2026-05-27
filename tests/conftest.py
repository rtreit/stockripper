"""Shared test fixtures.

All tests run with a clean environment so a developer `.env` cannot bleed
into deterministic test runs.
"""

from __future__ import annotations

from collections.abc import Iterator

import pytest

_STOCKRIPPER_ENV_VARS = (
    "ALPACA_API_KEY_ID",
    "ALPACA_API_SECRET_KEY",
    "ALPACA_BASE_URL",
    "ALPACA_DATA_URL",
    "OPENAI_API_KEY",
    "OPENAI_MODEL_DEFAULT",
    "OPENAI_MODEL_JUDGE",
    "DATABASE_URL",
    "STOCKRIPPER_ENV",
    "STOCKRIPPER_TIMEZONE",
)


@pytest.fixture(autouse=True)
def _isolated_env(
    monkeypatch: pytest.MonkeyPatch, tmp_path_factory: pytest.TempPathFactory
) -> Iterator[None]:
    for name in _STOCKRIPPER_ENV_VARS:
        monkeypatch.delenv(name, raising=False)
    # Force pydantic-settings to look at an empty temp directory so a developer
    # `.env` at the repo root never leaks into the test process.
    workdir = tmp_path_factory.mktemp("stockripper-env")
    monkeypatch.chdir(workdir)
    yield


@pytest.fixture
def paper_env(monkeypatch: pytest.MonkeyPatch) -> None:
    """Populate the minimum required env vars with safe paper-only values."""

    monkeypatch.setenv("ALPACA_API_KEY_ID", "PKTEST0000000000TEST")
    monkeypatch.setenv("ALPACA_API_SECRET_KEY", "test-secret-0000000000")
    monkeypatch.setenv("ALPACA_BASE_URL", "https://paper-api.alpaca.markets/v2")
    monkeypatch.setenv("OPENAI_API_KEY", "sk-test-0000000000")


from tests.fixtures_agents import (  # noqa: E402,F401 — re-export for collection
    now,
    sample_candidate,
    sample_market_climate,
    sample_packet,
    sample_recommendation,
    sample_risk_report,
    sample_run_input,
    sample_skeptic_report,
    sample_snapshot,
)
