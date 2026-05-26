"""Module entry point: ``python -m alpaca_mcp``."""

from __future__ import annotations

from alpaca_mcp.server import run


def main() -> None:
    run()


if __name__ == "__main__":  # pragma: no cover
    main()
