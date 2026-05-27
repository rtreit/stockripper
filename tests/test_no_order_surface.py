"""Structural ban: nothing under ``stockripper.data`` may import order-capable Alpaca surfaces.

Order submission/cancellation is a Phase 5 concern in
``stockripper.execution`` (not yet written). Phase 2 research code must not
even *import* the symbols that submit orders, so an accidental import is
caught at lint time rather than at runtime.
"""

from __future__ import annotations

import ast
from pathlib import Path

import pytest

# Anything in this set, imported anywhere under stockripper.data, is a bug.
_BANNED_NAMES = frozenset(
    {
        "OrderRequest",
        "MarketOrderRequest",
        "LimitOrderRequest",
        "StopOrderRequest",
        "StopLimitOrderRequest",
        "TrailingStopOrderRequest",
        "GetOptionContractsRequest",  # not an order but options-trading entry
        "ReplaceOrderRequest",
        "ClosePositionRequest",
    }
)

_BANNED_MODULES = frozenset(
    {
        "alpaca.broker.client",
    }
)

# ``alpaca.trading.client.TradingClient`` itself is allowed — but ONLY in the
# integrations factory module, which exposes it under a read-only Protocol.
_TRADING_CLIENT_ALLOWED_PATHS = (
    Path("src") / "stockripper" / "integrations" / "alpaca" / "__init__.py",
    Path("src") / "stockripper" / "data" / "live.py",
)


def _iter_data_python_files() -> list[Path]:
    repo = Path(__file__).resolve().parents[1]
    root = repo / "src" / "stockripper" / "data"
    return sorted(p for p in root.rglob("*.py"))


@pytest.mark.parametrize("path", _iter_data_python_files(), ids=lambda p: p.name)
def test_no_order_surface_import(path: Path) -> None:
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    for node in ast.walk(tree):
        if isinstance(node, ast.ImportFrom):
            module = node.module or ""
            if module in _BANNED_MODULES:
                pytest.fail(f"{path} imports banned module {module}")
            if module == "alpaca.trading.client":
                rel = path.relative_to(Path(__file__).resolve().parents[1])
                assert rel in _TRADING_CLIENT_ALLOWED_PATHS, (
                    f"{path} imports alpaca.trading.client (order-capable); "
                    "only the integrations factory or live wiring may do that."
                )
            for alias in node.names:
                if alias.name in _BANNED_NAMES:
                    pytest.fail(
                        f"{path} imports banned name {alias.name} from {module}"
                    )
        elif isinstance(node, ast.Import):
            for alias in node.names:
                if alias.name in _BANNED_MODULES:
                    pytest.fail(f"{path} imports banned module {alias.name}")
