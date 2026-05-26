"""Spawn and talk to the alpaca-mcp server from inside StockRipper agents.

The same MCP server that powers the Copilot CLI session is also the canonical
Alpaca tool surface for the StockRipper LangGraph agents. Sharing the server
gives us one place to add tools, one place to fix bugs, and one place to gate
live trading.

Defense-in-depth: when *we* spawn the server (i.e. from StockRipper application
code), we hard-code ``ALPACA_MODE=paper`` and strip ``ALPACA_ALLOW_LIVE`` from
the inherited environment. The StockRipper MVP is paper-only and that floor
must hold even if the user's ``~/.copilot/mcp-config.json`` is later flipped to
live for the CLI agent.

Typical use::

    async with AlpacaMcpClient.spawn() as client:
        tools = await client.list_tools()
        result = await client.call_tool("get_account", {})
"""

from __future__ import annotations

import os
import sys
from contextlib import asynccontextmanager
from pathlib import Path
from types import TracebackType
from typing import Any, Self

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.types import CallToolResult, Tool

# Resolve to the alpaca_mcp uv project living alongside this repo. This module
# lives at src/stockripper/agents/alpaca_mcp_client.py, so the repo root is
# parents[3] and the MCP project sits at <repo>/tools/alpaca_mcp.
ALPACA_MCP_DIRECTORY: Path = (
    Path(__file__).resolve().parents[3] / "tools" / "alpaca_mcp"
)

# Env keys we deliberately scrub from the inherited env so the spawned MCP
# server cannot accidentally enter live mode regardless of what the parent
# environment carries.
_LIVE_TRADING_KEYS: tuple[str, ...] = ("ALPACA_MODE", "ALPACA_ALLOW_LIVE")


class AlpacaMcpSpawnError(RuntimeError):
    """Raised when the alpaca-mcp server cannot be spawned or initialised."""


def build_alpaca_mcp_env(
    api_key_id: str,
    api_secret_key: str,
    *,
    base_env: dict[str, str] | None = None,
) -> dict[str, str]:
    """Return the environment dict used to spawn the MCP server.

    The base environment is the parent process env (or ``base_env`` for
    tests), with all live-trading knobs removed. Credentials and
    ``ALPACA_MODE=paper`` are then layered on top.
    """

    inherited = dict(base_env if base_env is not None else os.environ)
    for key in _LIVE_TRADING_KEYS:
        inherited.pop(key, None)
    inherited["ALPACA_API_KEY_ID"] = api_key_id
    inherited["ALPACA_API_SECRET_KEY"] = api_secret_key
    inherited["ALPACA_MODE"] = "paper"
    return inherited


def _default_command_args() -> tuple[str, list[str]]:
    """Return the ``(command, args)`` used to launch the MCP server.

    Uses ``uv --directory <path> run alpaca-mcp`` so the spawned process gets
    the right virtualenv regardless of where StockRipper is invoked from.
    """

    return (
        "uv",
        [
            "--directory",
            str(ALPACA_MCP_DIRECTORY),
            "run",
            "alpaca-mcp",
        ],
    )


class AlpacaMcpClient:
    """Thin async wrapper around an MCP ``ClientSession`` for the alpaca server.

    Use :meth:`spawn` (preferred) to start the server as a stdio subprocess
    with the paper-only safety env baked in. The class is an async context
    manager that initialises the session and tears it down cleanly.
    """

    def __init__(self, session: ClientSession) -> None:
        self._session = session
        self._tools: tuple[Tool, ...] | None = None

    # ------------------------------------------------------------------
    # Construction
    # ------------------------------------------------------------------
    @classmethod
    @asynccontextmanager
    async def spawn(
        cls,
        *,
        api_key_id: str,
        api_secret_key: str,
        command: str | None = None,
        args: list[str] | None = None,
        env: dict[str, str] | None = None,
    ) -> Any:
        """Async context manager that spawns alpaca-mcp and yields a ready client.

        Parameters allow override for tests (e.g., point at a stub server) but
        the defaults run the real server from ``tools/alpaca_mcp/``. The
        ``env`` override is layered on top of the safety-scrubbed base env, so
        callers cannot accidentally re-introduce live-trading knobs without
        explicitly passing them in.
        """

        if not ALPACA_MCP_DIRECTORY.exists():
            raise AlpacaMcpSpawnError(
                f"alpaca-mcp project not found at {ALPACA_MCP_DIRECTORY}"
            )

        default_cmd, default_args = _default_command_args()
        spawn_command = command or default_cmd
        spawn_args = args if args is not None else default_args
        spawn_env = build_alpaca_mcp_env(api_key_id, api_secret_key)
        if env:
            spawn_env.update(env)

        params = StdioServerParameters(
            command=spawn_command,
            args=spawn_args,
            env=spawn_env,
        )
        try:
            async with (
                stdio_client(params) as (read, write),
                ClientSession(read, write) as session,
            ):
                await session.initialize()
                yield cls(session)
        except AlpacaMcpSpawnError:
            raise
        except Exception as exc:
            raise AlpacaMcpSpawnError(
                f"failed to spawn alpaca-mcp: {exc}"
            ) from exc

    # ------------------------------------------------------------------
    # Tool API
    # ------------------------------------------------------------------
    async def list_tools(self, *, refresh: bool = False) -> tuple[Tool, ...]:
        """Return all tools exposed by the server (cached unless ``refresh``)."""

        if self._tools is not None and not refresh:
            return self._tools
        result = await self._session.list_tools()
        self._tools = tuple(result.tools)
        return self._tools

    async def tool_names(self, *, refresh: bool = False) -> tuple[str, ...]:
        """Return only the tool names (convenience for agent registries)."""

        return tuple(t.name for t in await self.list_tools(refresh=refresh))

    async def call_tool(
        self, name: str, arguments: dict[str, Any] | None = None
    ) -> CallToolResult:
        """Invoke ``name`` on the server and return the raw MCP result."""

        return await self._session.call_tool(name, arguments or {})

    # ------------------------------------------------------------------
    # Direct (non-context-manager) lifecycle support — mainly for tests
    # that want to assert on the session object explicitly.
    # ------------------------------------------------------------------
    @property
    def session(self) -> ClientSession:
        return self._session

    async def __aenter__(self) -> Self:
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> None:
        # The session itself is owned by ``stdio_client`` / ``ClientSession``
        # context managers in :meth:`spawn`; nothing to tear down here.
        _ = (exc_type, exc, tb)


def _import_self_check() -> None:
    """Tiny no-op kept so ``python -m`` imports don't trip ``F401`` warnings."""

    _ = sys.modules[__name__]
