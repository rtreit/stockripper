"""Smoke tests for the Typer CLI entry point."""

from __future__ import annotations

from pathlib import Path

import pytest
from typer.testing import CliRunner

from stockripper.__main__ import app


def test_cli_version_prints_version() -> None:
    runner = CliRunner()
    result = runner.invoke(app, ["version"])
    assert result.exit_code == 0
    assert "stockripper" in result.stdout.lower()


def test_cli_status_fails_without_credentials() -> None:
    runner = CliRunner()
    result = runner.invoke(app, ["status"])
    assert result.exit_code == 1
    assert "configuration error" in result.stdout.lower()


def test_cli_status_succeeds_with_paper_env(paper_env: None) -> None:
    runner = CliRunner()
    result = runner.invoke(app, ["status"])
    assert result.exit_code == 0, result.stdout
    assert "paper-endpoint check passed" in result.stdout.lower()
    # Secret values must never appear in the status output.
    assert "test-secret-0000000000" not in result.stdout


def test_cli_tracks_list_prints_all_tracks(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("COLUMNS", "240")
    runner = CliRunner()
    result = runner.invoke(app, ["tracks", "list"])
    assert result.exit_code == 0, result.stdout
    for name in ("conservative", "aggressive", "yolo", "benchmark"):
        assert name in result.stdout.lower()


def test_cli_db_init_creates_sqlite_schema(tmp_path: Path) -> None:
    db_path = tmp_path / "ledger.db"
    url = f"sqlite:///{db_path.as_posix()}"
    runner = CliRunner()
    result = runner.invoke(app, ["db", "init", "--database-url", url])
    assert result.exit_code == 0, result.stdout
    assert db_path.exists()

    # Seed step should be idempotent and report 8/8.
    seed = runner.invoke(app, ["tracks", "seed", "--database-url", url])
    assert seed.exit_code == 0, seed.stdout
    assert "8 risk policies" in seed.stdout
    assert "8 strategy tracks" in seed.stdout
