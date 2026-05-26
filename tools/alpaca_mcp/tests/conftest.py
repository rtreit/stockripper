"""Test fixtures: isolate env vars so the developer .env never leaks in."""

from __future__ import annotations

from collections.abc import Iterator

import pytest

_VARS = (
    "ALPACA_API_KEY_ID",
    "ALPACA_API_SECRET_KEY",
    "ALPACA_MODE",
    "ALPACA_ALLOW_LIVE",
)


@pytest.fixture(autouse=True)
def _isolated_env(
    monkeypatch: pytest.MonkeyPatch, tmp_path_factory: pytest.TempPathFactory
) -> Iterator[None]:
    for name in _VARS:
        monkeypatch.delenv(name, raising=False)
    workdir = tmp_path_factory.mktemp("alpaca-mcp-env")
    monkeypatch.chdir(workdir)
    yield


@pytest.fixture
def paper_creds(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ALPACA_API_KEY_ID", "PKTEST0000000000")
    monkeypatch.setenv("ALPACA_API_SECRET_KEY", "test-secret")


@pytest.fixture
def live_armed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ALPACA_API_KEY_ID", "AKTEST0000000000")
    monkeypatch.setenv("ALPACA_API_SECRET_KEY", "test-secret")
    monkeypatch.setenv("ALPACA_MODE", "live")
    monkeypatch.setenv("ALPACA_ALLOW_LIVE", "true")
