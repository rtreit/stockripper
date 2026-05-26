# StockRipper

> Autonomous multi-agent paper-trading research laboratory.

StockRipper runs a council of LLM-driven agents across competing strategy
tracks (`conservative` → `balanced` → `aggressive` → `concentrated` → `yolo`,
plus `quant_signal`, `random_baseline`, and `benchmark`), executing
**fully autonomously** against the Alpaca paper-trading environment. The
headline output is a head-to-head leaderboard of strategy tracks.

The authoritative spec lives in [`PROJECT_SPEC.md`](./PROJECT_SPEC.md).

## Safety floor

StockRipper is paper-only by design. The application **refuses to start**
against any Alpaca endpoint other than `paper-api.alpaca.markets`. This is
enforced in `src/stockripper/config.py`, covered by tests in
`tests/test_config.py`, and additionally guarded by a pre-tool hook in
`.github/hooks/scripts/pre-tool-policy.ps1` that blocks edits referencing the
live endpoint.

## Quick start

Prerequisites: [uv](https://docs.astral.sh/uv/) (Python ≥ 3.12) and Docker
Desktop for the local Postgres.

```powershell
# 1. Install dependencies into a project-local venv
uv sync --dev

# 2. Create your local .env (gitignored)
Copy-Item .env.example .env
# then edit .env and fill in:
#   ALPACA_API_KEY_ID, ALPACA_API_SECRET_KEY
#   OPENAI_API_KEY

# 3. Bring up Postgres
docker compose up -d postgres

# 4. Verify the paper-endpoint check + config load
uv run stockripper status

# 5. Run the test suite
uv run pytest
```

`uv run stockripper status` prints a **redacted** view of your configuration
and confirms that the paper-endpoint fail-closed check passes. It exits
non-zero if any required credential is missing or if `ALPACA_BASE_URL` does
not resolve to `paper-api.alpaca.markets`.

## Repository layout (Phase 0)

```
PROJECT_SPEC.md           Authoritative spec
.github/                  Copilot customization + CI workflows + safety hooks
docker-compose.yml        Local Postgres (Phase 1+)
pyproject.toml            uv-managed project + tool configuration
src/stockripper/
  __init__.py
  __main__.py             Typer CLI (`stockripper version|status`)
  config.py               pydantic-settings with paper-endpoint fail-closed
tests/
  conftest.py             Env-isolated fixtures
  test_config.py          Universal floor #1 invariants
  test_cli.py             CLI smoke tests
```

Later phases add the Alpaca client, ledger, agent council, LangGraph
orchestration, per-track risk gates, scoring engine, leaderboard, and
backtest harness — see roadmap in `PROJECT_SPEC.md` §25.

## Development commands

```powershell
uv sync --dev                       # install / refresh deps
uv run ruff check .                 # lint
uv run ruff check --fix .           # lint + autofix
uv run mypy src/stockripper tests   # type check (strict)
uv run pytest                       # all tests
uv run pytest -m "not live"         # exclude live-network tests
uv lock --check                     # verify lockfile is in sync
```

## License

MIT — see [`LICENSE`](./LICENSE).
