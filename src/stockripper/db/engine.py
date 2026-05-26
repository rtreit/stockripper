"""Engine + session factory for the StockRipper ledger."""

from __future__ import annotations

from collections.abc import Iterator
from contextlib import contextmanager

from sqlalchemy import Engine, create_engine
from sqlalchemy.orm import Session, sessionmaker

from stockripper.config import load_settings

_DEFAULT_SQLITE_URL = "sqlite:///stockripper.db"


def _resolve_url(database_url: str | None) -> str:
    """Pick the database URL with the documented precedence.

    Explicit argument > settings.database_url. If config can't load (e.g.,
    missing required env vars in a test), fall back to a local SQLite file.
    """

    if database_url:
        return database_url

    try:
        settings = load_settings()
    except Exception:
        return _DEFAULT_SQLITE_URL
    return settings.database_url or _DEFAULT_SQLITE_URL


def build_engine(
    database_url: str | None = None, *, echo: bool = False,
) -> Engine:
    """Construct a SQLAlchemy engine using the resolved URL."""

    url = _resolve_url(database_url)
    # SQLite needs ``check_same_thread`` relaxed for tests that share a
    # connection across threads; harmless on Postgres because the option is
    # silently dropped by the dialect.
    connect_args: dict[str, object] = {}
    if url.startswith("sqlite"):
        connect_args["check_same_thread"] = False
    return create_engine(url, echo=echo, future=True, connect_args=connect_args)


def build_session_factory(
    engine: Engine | None = None,
) -> sessionmaker[Session]:
    """Return a session factory bound to ``engine`` (or a freshly built one)."""

    bound = engine if engine is not None else build_engine()
    return sessionmaker(
        bound, class_=Session, expire_on_commit=False, autoflush=False,
    )


@contextmanager
def session_scope(
    factory: sessionmaker[Session] | None = None,
) -> Iterator[Session]:
    """Context-manager that commits on exit and rolls back on exception."""

    fac = factory if factory is not None else build_session_factory()
    session = fac()
    try:
        yield session
        session.commit()
    except Exception:
        session.rollback()
        raise
    finally:
        session.close()
