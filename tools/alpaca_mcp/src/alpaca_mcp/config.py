"""Configuration and the live-trading double-confirmation safety gate.

Modes:
    * ``paper`` (default) — safe; uses ``paper-api.alpaca.markets``.
    * ``live``            — only permitted when ``ALPACA_ALLOW_LIVE=true`` is
                            *also* set. The double opt-in makes "I'm in live"
                            an intentional act, not a config typo.

The StockRipper main app refuses live unconditionally (see
``src/stockripper/config.py``). This MCP server is the only place in the
repository allowed to talk to a live endpoint, and only when the operator
explicitly arms it.
"""

from __future__ import annotations

from enum import StrEnum
from typing import Final

from pydantic import Field, SecretStr, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class AlpacaMode(StrEnum):
    PAPER = "paper"
    LIVE = "live"


PAPER_TRADING_URL: Final[str] = "https://paper-api.alpaca.markets"
LIVE_TRADING_URL: Final[str] = "https://api.alpaca.markets"


class LiveTradingGateError(RuntimeError):
    """Raised when ``mode=live`` is requested without the explicit opt-in."""


class AlpacaMcpSettings(BaseSettings):
    """Configuration loaded from environment variables and optional ``.env``.

    All credentials are wrapped in :class:`SecretStr` so they cannot be leaked
    via ``repr``.
    """

    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
        case_sensitive=False,
        populate_by_name=True,
    )

    alpaca_api_key_id: SecretStr = Field(..., alias="ALPACA_API_KEY_ID")
    alpaca_api_secret_key: SecretStr = Field(..., alias="ALPACA_API_SECRET_KEY")
    alpaca_mode: AlpacaMode = Field(default=AlpacaMode.PAPER, alias="ALPACA_MODE")
    alpaca_allow_live: bool = Field(default=False, alias="ALPACA_ALLOW_LIVE")

    @model_validator(mode="after")
    def _enforce_live_gate(self) -> AlpacaMcpSettings:
        if self.alpaca_mode is AlpacaMode.LIVE and not self.alpaca_allow_live:
            raise LiveTradingGateError(
                "ALPACA_MODE=live requires ALPACA_ALLOW_LIVE=true to be set in the "
                "same environment. This is intentional double-confirmation — "
                "live trading risks real money."
            )
        return self

    @property
    def is_paper(self) -> bool:
        return self.alpaca_mode is AlpacaMode.PAPER

    @property
    def trading_base_url(self) -> str:
        return PAPER_TRADING_URL if self.is_paper else LIVE_TRADING_URL

    def banner(self) -> str:
        """Return a loud, human-readable mode banner for startup logging."""

        if self.is_paper:
            return (
                "==================================================\n"
                "  Alpaca MCP server - mode: PAPER (safe)\n"
                f"  Trading endpoint: {self.trading_base_url}\n"
                "=================================================="
            )
        return (
            "!!================================================!!\n"
            "  Alpaca MCP server - mode: LIVE   ** REAL MONEY **\n"
            f"  Trading endpoint: {self.trading_base_url}\n"
            "  ALPACA_ALLOW_LIVE=true is set. Be careful.\n"
            "!!================================================!!"
        )


def load_settings() -> AlpacaMcpSettings:
    """Load and validate settings. Raises on missing creds or unarmed live mode."""

    return AlpacaMcpSettings()  # type: ignore[call-arg]


__all__ = (
    "LIVE_TRADING_URL",
    "PAPER_TRADING_URL",
    "AlpacaMcpSettings",
    "AlpacaMode",
    "LiveTradingGateError",
    "load_settings",
)
