# Alpaca MCP server

A [Model Context Protocol](https://modelcontextprotocol.io/) server that
exposes the Alpaca trading and market-data APIs as MCP tools. Designed for
research and trading workflows: wire it into the Copilot CLI (or any
MCP-compatible client) and ask the agent to call Alpaca on your behalf.

This package is intentionally separate from the main StockRipper application:

- The StockRipper main app (`src/stockripper/`) refuses live trading
  unconditionally. That is non-negotiable for the MVP.
- This MCP server supports **paper or live** trading, gated behind a
  double opt-in. Use it for research, exploration, or eventual live use
  outside the StockRipper autonomous loop.

## Safety: live trading is double-gated

The server only enters live mode when *both* of these env vars are set:

```
ALPACA_MODE=live
ALPACA_ALLOW_LIVE=true
```

Setting only one leaves the server in paper mode. A loud banner is printed
to stderr at startup announcing which mode is active.

## Install

From the repo root:

```powershell
cd tools\alpaca_mcp
uv sync --dev
```

## Configure credentials

The server reads the same Alpaca credentials as the main app, so a `.env`
at the repo root works:

```
ALPACA_API_KEY_ID=PK...        # paper key starts with PK, live key with AK
ALPACA_API_SECRET_KEY=...
ALPACA_MODE=paper              # default; switch to "live" only with both knobs
# ALPACA_ALLOW_LIVE=true       # only set when you really mean it
```

## Run standalone (stdio transport)

```powershell
uv run alpaca-mcp
# or: uv run python -m alpaca_mcp
```

The server speaks MCP over stdio, which is the format every MCP client
expects.

## Register with Copilot CLI

Use the `/mcp` slash command to add the server to your Copilot CLI
configuration. A working config entry looks like:

```json
{
  "mcpServers": {
    "alpaca": {
      "command": "uv",
      "args": [
        "--directory",
        "C:/wdgit/stockripper/tools/alpaca_mcp",
        "run",
        "alpaca-mcp"
      ],
      "env": {
        "ALPACA_MODE": "paper"
      }
    }
  }
}
```

Restart the CLI session after editing the config so the new tools register.

## Tools exposed

Account / clock / calendar:
`get_account`, `get_clock`, `get_calendar`

Positions:
`get_positions`, `get_position`, `close_position`, `close_all_positions`

Orders:
`get_orders`, `get_order`, `submit_order`, `replace_order`,
`cancel_order`, `cancel_all_orders`

Portfolio / assets:
`get_portfolio_history`, `get_assets`, `get_asset`

Stock market data:
`get_stock_bars`, `get_stock_latest_quote`, `get_stock_latest_trade`,
`get_stock_snapshot`

News: `get_news`

Options:
`get_option_contracts`, `get_option_chain`, `get_option_snapshot`,
`get_option_latest_quote`, `get_option_bars`

Diagnostic: `alpaca_mode`

## Develop

```powershell
uv run ruff check .
uv run mypy src/alpaca_mcp tests
uv run pytest
```

Tests are network-free and verify the safety gate plus the tool wiring.
