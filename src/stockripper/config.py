"""Configuration and environment handling for StockRipper.

The single most important guarantee in this module is the **paper-endpoint
fail-closed check**: StockRipper must refuse to start against any Alpaca
endpoint other than ``paper-api.alpaca.markets``. This corresponds to
universal floor #1 in ``PROJECT_SPEC.md`` (§16.1) and is enforced both at
config load time and at every call site that reads ``alpaca_base_url``.

Credentials are stored as :class:`pydantic.SecretStr` so they cannot be
accidentally logged via ``repr``. LLM agents must never receive raw
credential values — only well-typed clients constructed from them.
"""

from __future__ import annotations

from typing import Final
from urllib.parse import urlparse

from pydantic import Field, SecretStr, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

PAPER_HOSTS: Final[frozenset[str]] = frozenset({"paper-api.alpaca.markets"})
"""Hostnames allowed for the Alpaca trading endpoint.

Live trading hosts such as ``api.alpaca.markets`` are intentionally excluded.
Widening this set must be paired with the live-trading graduation process in
``PROJECT_SPEC.md`` §16.6 / §28.4.
"""


class PaperEndpointError(ValueError):
    """Raised when a non-paper Alpaca endpoint is configured."""


def _assert_paper_host(url: str, *, field_name: str) -> str:
    parsed = urlparse(url)
    host = (parsed.hostname or "").lower()
    if host not in PAPER_HOSTS:
        raise PaperEndpointError(
            f"{field_name}={url!r} resolves to host {host!r}, which is not a "
            f"permitted paper endpoint. Allowed paper hosts: "
            f"{sorted(PAPER_HOSTS)!r}. Live trading is not permitted in the MVP."
        )
    if parsed.scheme != "https":
        raise PaperEndpointError(
            f"{field_name}={url!r} must use https (got scheme {parsed.scheme!r})."
        )
    return url


class StockripperSettings(BaseSettings):
    """Top-level settings, loaded from environment variables and ``.env``.

    All trading-relevant fields are validated. Credential fields are wrapped
    in :class:`SecretStr` so that ``repr`` and structured logging cannot leak
    them by accident.
    """

    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
        case_sensitive=False,
        populate_by_name=True,
    )

    # --- Alpaca paper (required at runtime) ---
    alpaca_api_key_id: SecretStr = Field(..., alias="ALPACA_API_KEY_ID")
    alpaca_api_secret_key: SecretStr = Field(..., alias="ALPACA_API_SECRET_KEY")
    alpaca_base_url: str = Field(
        default="https://paper-api.alpaca.markets/v2",
        alias="ALPACA_BASE_URL",
    )
    alpaca_data_url: str = Field(
        default="https://data.alpaca.markets/v2",
        alias="ALPACA_DATA_URL",
    )

    # --- LLM provider ---
    openai_api_key: SecretStr = Field(..., alias="OPENAI_API_KEY")
    openai_model_default: str = Field(default="gpt-5-nano", alias="OPENAI_MODEL_DEFAULT")
    openai_model_judge: str = Field(default="gpt-5-mini", alias="OPENAI_MODEL_JUDGE")

    # --- Database ---
    database_url: str = Field(
        default="postgresql+psycopg://stockripper:stockripper@localhost:5432/stockripper",
        alias="DATABASE_URL",
    )

    # --- Runtime ---
    stockripper_env: str = Field(default="local", alias="STOCKRIPPER_ENV")
    stockripper_timezone: str = Field(
        default="America/New_York", alias="STOCKRIPPER_TIMEZONE"
    )

    @field_validator("alpaca_base_url")
    @classmethod
    def _validate_alpaca_base_url(cls, value: str) -> str:
        return _assert_paper_host(value, field_name="ALPACA_BASE_URL")

    def assert_paper_only(self) -> None:
        """Re-check the paper-endpoint invariant.

        Intended to be called at startup and at the entry of any
        execution-adapter code path so the universal floor is enforced
        defensively even if the settings object is rebuilt or patched.
        """
        _assert_paper_host(self.alpaca_base_url, field_name="ALPACA_BASE_URL")


def load_settings() -> StockripperSettings:
    """Load and validate settings from the environment.

    Raises :class:`PaperEndpointError` if a non-paper Alpaca endpoint is
    configured, and :class:`pydantic.ValidationError` if required credentials
    are missing.
    """

    return StockripperSettings()  # type: ignore[call-arg]


def redact_secrets(settings: StockripperSettings) -> dict[str, str]:
    """Return a logging-safe view of the settings with all secrets redacted."""

    def _mask(value: SecretStr) -> str:
        raw = value.get_secret_value()
        if not raw:
            return "<empty>"
        if len(raw) <= 4:
            return "***"
        return f"{raw[:2]}***{raw[-2:]}"

    return {
        "alpaca_api_key_id": _mask(settings.alpaca_api_key_id),
        "alpaca_api_secret_key": "***",
        "alpaca_base_url": settings.alpaca_base_url,
        "alpaca_data_url": settings.alpaca_data_url,
        "openai_api_key": _mask(settings.openai_api_key),
        "openai_model_default": settings.openai_model_default,
        "openai_model_judge": settings.openai_model_judge,
        "database_url": _safe_db_url(settings.database_url),
        "stockripper_env": settings.stockripper_env,
        "stockripper_timezone": settings.stockripper_timezone,
    }


def _safe_db_url(url: str) -> str:
    """Mask any embedded password in a SQLAlchemy-style URL for logging."""

    try:
        parsed = urlparse(url)
    except ValueError:
        return "<unparseable>"
    if parsed.password is None:
        return url
    netloc = parsed.netloc.replace(f":{parsed.password}@", ":***@")
    return parsed._replace(netloc=netloc).geturl()


__all__ = (
    "PAPER_HOSTS",
    "PaperEndpointError",
    "StockripperSettings",
    "load_settings",
    "redact_secrets",
)
