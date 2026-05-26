---
name: alpaca-mcp
description: >-
  Use the local Alpaca MCP server (tools/alpaca_mcp) to read account state,
  market data, news, options chains, and to place/replace/cancel paper orders
  during agent runs and ad-hoc research. Invoke when the user asks about
  current positions, recent fills, equity history, what the market is doing
  right now, news for a symbol, available option contracts, or wants to place
  a paper trade through Copilot rather than through the StockRipper app.
---

# Alpaca MCP

A standalone uv-managed MCP server at `tools/alpaca_mcp/` that wraps the
official `alpaca-py` SDK and exposes ~27 tools across trading, market data,
news, and options. It is a separate process from the main StockRipper app and
shares no code with it.

## When to use

Trigger this skill when the task involves any of:

- Live introspection of the Alpaca account: equity, buying power, cash,
  status, day-trade count.
- Listing or inspecting current positions, open/closed orders, portfolio
  history, account activities.
- Pulling stock market data: latest quote/trade, OHLCV bars, snapshots.
- Pulling option market data: chains, contract metadata, snapshots, bars.
- Reading Alpaca News API articles for one or more symbols.
- Submitting, replacing, or cancelling paper orders interactively as part of
  research or debugging (the main StockRipper app uses its own clients for
  scheduled/autonomous trading).

Prefer the MCP server over hand-rolled `httpx` calls or shelling into the
SDK. The tools normalize responses to JSON and surface useful errors.

## When NOT to use

- Long-running autonomous trading loops. Those belong to the StockRipper
  application code under `src/stockripper/`, which has its own clients,
  rate-limit handling, ledger writes, and policy enforcement.
- Anything that requires writes to the StockRipper Postgres ledger.
- Anything outside Alpaca's APIs (sentiment vendors, news vendors other than
  Alpaca News, broker comparisons, etc.).

## Safety model (important)

- The server defaults to **paper** (`https://paper-api.alpaca.markets`).
- Live trading requires BOTH `ALPACA_MODE=live` AND `ALPACA_ALLOW_LIVE=true`
  set in the same environment. Setting one without the other keeps the
  server in paper. Setting `ALPACA_MODE=live` alone fails fast with
  `LiveTradingGateError` before the stdio server starts.
- On startup the server prints a loud banner to stderr stating the active
  mode and trading endpoint.
- This server is intentionally outside the main StockRipper app so the
  paper-only invariant in `src/stockripper/config.py` is never weakened.
  The pre-tool-policy hook is scoped to skip `tools/alpaca_mcp/` precisely
  because this package legitimately references both endpoints.

## How to invoke

1. From a fresh terminal:
   ```powershell
   cd tools\alpaca_mcp
   uv sync --dev
   uv run alpaca-mcp
   ```
2. From Copilot CLI, register the server once via `/mcp` using the JSON
   snippet in `tools/alpaca_mcp/README.md`, then restart the session. The
   tools appear as `mcp__alpaca__*` (e.g. `mcp__alpaca__get_account`,
   `mcp__alpaca__submit_order`).

## Key tools (representative subset)

- Account: `get_account`, `alpaca_mode`, `get_clock`, `get_calendar`.
- Positions: `get_positions`, `get_position`, `close_position`,
  `close_all_positions`.
- Orders: `get_orders`, `get_order`, `submit_order`, `replace_order`,
  `cancel_order`, `cancel_all_orders`.
- Portfolio: `get_portfolio_history`.
- Assets: `get_assets`, `get_asset`.
- Stock data: `get_stock_bars`, `get_stock_latest_quote`,
  `get_stock_latest_trade`, `get_stock_snapshot`.
- News: `get_news`.
- Options: `get_option_contracts`, `get_option_chain`, `get_option_snapshot`,
  `get_option_latest_quote`, `get_option_bars`.

## Gotchas

- `submit_order` requires exactly one of `qty` or `notional`. Trailing-stop
  branches are not yet wired; pass nothing for `trail_price`/`trail_percent`.
- `replace_order` takes `qty` as an integer (not fractional). Use
  `close_position` with `percentage` if you need fractional liquidation.
- `get_news` joins symbol lists into a comma-separated string before calling
  the SDK; both `"AAPL,MSFT"` and `["AAPL","MSFT"]` work.
- Timeframe strings on bar tools accept `1Min|5Min|15Min|30Min|1Hour|1Day|1Week|1Month`
  (case-insensitive). Unknown values fall back to `1Day` rather than raising.
- Bar responses are unwrapped via `getattr(result, "data", result)` so you
  get the symbol-keyed dict directly.

## Reference

- Package: `tools/alpaca_mcp/`
- Entry point: `tools/alpaca_mcp/src/alpaca_mcp/server.py`
- Config + safety gate: `tools/alpaca_mcp/src/alpaca_mcp/config.py`
- Tests (15, all green): `tools/alpaca_mcp/tests/`
- README + `/mcp` JSON: `tools/alpaca_mcp/README.md`
- Spec context: `PROJECT_SPEC.md` (paper-only floor for the main app)
