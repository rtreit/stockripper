"""Internal Alpaca client factories shared by data adapters.

This package is the *internal* boundary above ``alpaca-py``. The MCP server
(``tools/alpaca_mcp``) remains the agent-facing surface; this module is the
research/data-ingestion path used by the universe builder and friends.

Critical guarantee: nothing in :mod:`stockripper.data` may import the
trading client from here. The trading-client factory lives in
:mod:`stockripper.execution` (Phase 5) so research code is structurally
incapable of submitting orders.
"""
