"""CLI entry point.

Phase 0 added ``version``/``status``/``check-llm``. Phase 1 adds the
``db`` and ``tracks`` subcommand groups plus a ``reconcile`` command that
spawns the alpaca-mcp client and writes a track snapshot per enabled track.
"""

from __future__ import annotations

import asyncio
import datetime as dt
import sys
from dataclasses import dataclass
from pathlib import Path

import typer
from alembic import command as alembic_command
from alembic.config import Config as AlembicConfig
from rich.console import Console
from rich.table import Table

from stockripper._version import __version__
from stockripper.agents import reconcile_via_mcp
from stockripper.config import (
    PaperEndpointError,
    StockripperSettings,
    load_settings,
    redact_secrets,
)
from stockripper.db import Base, build_engine, session_scope
from stockripper.risk import DEFAULT_RISK_POLICIES
from stockripper.tracks import DEFAULT_TRACKS, seed_default_tracks

app = typer.Typer(
    help="StockRipper — autonomous multi-agent paper-trading research laboratory.",
    no_args_is_help=True,
)
db_app = typer.Typer(help="Database schema management (Alembic + dev shortcuts).")
tracks_app = typer.Typer(help="Strategy-track inspection and seeding.")
universe_app = typer.Typer(help="Candidate universe construction and inspection.")
research_app = typer.Typer(help="Per-symbol research probes (fundamentals, news).")
app.add_typer(db_app, name="db")
app.add_typer(tracks_app, name="tracks")
app.add_typer(universe_app, name="universe")
app.add_typer(research_app, name="research")
console = Console()


_ALEMBIC_INI = Path(__file__).resolve().parents[2] / "alembic.ini"


@app.command()
def version() -> None:
    """Print the StockRipper version."""

    console.print(f"stockripper {__version__}")


@app.command()
def status() -> None:
    """Load configuration and print a redacted summary.

    Fails fast (exit code 1) if the configured Alpaca endpoint is not a paper
    endpoint, or if required credentials are missing.
    """

    try:
        settings = load_settings()
        settings.assert_paper_only()
    except PaperEndpointError as exc:
        console.print(f"[bold red]Paper-endpoint check failed:[/] {exc}")
        sys.exit(1)
    except Exception as exc:  # top-level CLI guard
        console.print(f"[bold red]Configuration error:[/] {exc}")
        sys.exit(1)

    table = Table(title="StockRipper configuration", show_header=True)
    table.add_column("setting", style="bold cyan")
    table.add_column("value")
    for key, value in redact_secrets(settings).items():
        table.add_row(key, value)
    console.print(table)
    console.print("[bold green]Paper-endpoint check passed.[/]")


@dataclass(frozen=True)
class _LlmProbe:
    role: str
    model: str
    ok: bool
    detail: str
    input_tokens: int | None = None
    output_tokens: int | None = None


def _probe_openai_model(settings: StockripperSettings, *, role: str, model: str) -> _LlmProbe:
    """Send a tiny ping to the given model and return a structured result.

    Uses the Responses API which is the recommended surface for the gpt-5
    family. Imports the SDK lazily so the CLI keeps working in environments
    without the OpenAI package installed.
    """

    try:
        from openai import OpenAI
    except ImportError as exc:  # pragma: no cover - environment guard
        return _LlmProbe(role=role, model=model, ok=False, detail=f"openai SDK missing: {exc}")

    client = OpenAI(api_key=settings.openai_api_key.get_secret_value())

    try:
        response = client.responses.create(
            model=model,
            input=(
                "Reply with exactly the single word PONG. "
                "Do not add any punctuation or explanation."
            ),
            max_output_tokens=2048,
        )
    except Exception as exc:  # network / auth / model errors all collapse here
        return _LlmProbe(role=role, model=model, ok=False, detail=f"{type(exc).__name__}: {exc}")

    text = (response.output_text or "").strip()
    usage = getattr(response, "usage", None)
    input_tokens = getattr(usage, "input_tokens", None) if usage else None
    output_tokens = getattr(usage, "output_tokens", None) if usage else None

    if "pong" not in text.lower():
        return _LlmProbe(
            role=role,
            model=model,
            ok=False,
            detail=f"unexpected reply: {text!r}",
            input_tokens=input_tokens,
            output_tokens=output_tokens,
        )

    return _LlmProbe(
        role=role,
        model=model,
        ok=True,
        detail=text,
        input_tokens=input_tokens,
        output_tokens=output_tokens,
    )


@app.command(name="check-llm")
def check_llm() -> None:
    """Round-trip both configured OpenAI models with a tiny prompt.

    Exits 0 only if every probe succeeds. Designed as a fast operator
    diagnostic; not part of the automated test suite.
    """

    try:
        settings = load_settings()
    except Exception as exc:  # top-level CLI guard
        console.print(f"[bold red]Configuration error:[/] {exc}")
        sys.exit(1)

    probes = [
        _probe_openai_model(settings, role="default", model=settings.openai_model_default),
        _probe_openai_model(settings, role="judge", model=settings.openai_model_judge),
    ]

    table = Table(title="OpenAI model probes", show_header=True)
    table.add_column("role", style="bold cyan")
    table.add_column("model")
    table.add_column("status")
    table.add_column("reply / error")
    table.add_column("in tok", justify="right")
    table.add_column("out tok", justify="right")
    for p in probes:
        status_text = "[green]ok[/]" if p.ok else "[red]fail[/]"
        table.add_row(
            p.role,
            p.model,
            status_text,
            p.detail,
            "-" if p.input_tokens is None else str(p.input_tokens),
            "-" if p.output_tokens is None else str(p.output_tokens),
        )
    console.print(table)

    if not all(p.ok for p in probes):
        sys.exit(1)


# ---------------------------------------------------------------------------
# db subcommands
# ---------------------------------------------------------------------------
def _alembic_config(database_url: str | None = None) -> AlembicConfig:
    cfg = AlembicConfig(str(_ALEMBIC_INI))
    if database_url is not None:
        cfg.set_main_option("sqlalchemy.url", database_url)
    return cfg


@db_app.command("init")
def db_init(
    database_url: str | None = typer.Option(
        None,
        "--database-url",
        help="Override DATABASE_URL for this command (useful for local SQLite).",
    ),
) -> None:
    """Create every ledger table directly from ORM metadata.

    Skips Alembic so a brand-new clone can stand up a usable schema before
    Postgres is even running. Use ``db upgrade`` once you want migration
    history applied.
    """

    engine = build_engine(database_url)
    Base.metadata.create_all(bind=engine)
    console.print(f"[bold green]Schema created at[/] {engine.url}")


@db_app.command("upgrade")
def db_upgrade(
    revision: str = typer.Argument("head", help="Target Alembic revision."),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Apply Alembic migrations up to ``revision`` (default: head)."""

    alembic_command.upgrade(_alembic_config(database_url), revision)
    console.print(f"[bold green]Upgraded to[/] {revision}")


@db_app.command("downgrade")
def db_downgrade(
    revision: str = typer.Argument(..., help="Target Alembic revision (e.g. base or -1)."),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Downgrade the schema to ``revision``."""

    alembic_command.downgrade(_alembic_config(database_url), revision)
    console.print(f"[bold yellow]Downgraded to[/] {revision}")


@db_app.command("current")
def db_current(
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Show the current Alembic revision applied to the database."""

    alembic_command.current(_alembic_config(database_url), verbose=True)


# ---------------------------------------------------------------------------
# tracks subcommands
# ---------------------------------------------------------------------------
@tracks_app.command("list")
def tracks_list() -> None:
    """Print every seeded strategy track and its risk-policy linkage."""

    table = Table(title="Strategy tracks", show_header=True)
    table.add_column("track_id", style="bold cyan")
    table.add_column("name")
    table.add_column("risk policy")
    table.add_column("judge objective")
    table.add_column("starting equity", justify="right")
    table.add_column("enabled", justify="center")
    for spec in DEFAULT_TRACKS:
        table.add_row(
            spec.track_id,
            spec.name,
            spec.risk_policy_id,
            spec.judge_objective,
            f"${spec.starting_equity_usd:,.0f}",
            "✓" if spec.enabled else "✗",
        )
    console.print(table)
    console.print(
        f"[dim]{len(DEFAULT_RISK_POLICIES)} risk policies / "
        f"{len(DEFAULT_TRACKS)} tracks defined in code.[/]"
    )


@tracks_app.command("seed")
def tracks_seed(
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Idempotently insert the default risk policies + tracks into the DB."""

    engine = build_engine(database_url)
    from sqlalchemy.orm import sessionmaker

    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)
    with session_scope(factory) as session:
        policies, tracks = seed_default_tracks(session)
    console.print(
        f"[bold green]Seeded[/] {policies} risk policies, {tracks} strategy tracks."
    )


# ---------------------------------------------------------------------------
# reconcile
# ---------------------------------------------------------------------------
@app.command()
def reconcile(
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Spawn the alpaca-mcp client, pull account/orders, and write a snapshot.

    Fails fast on a non-paper endpoint or missing credentials.
    """

    try:
        settings = load_settings()
        settings.assert_paper_only()
    except PaperEndpointError as exc:
        console.print(f"[bold red]Paper-endpoint check failed:[/] {exc}")
        sys.exit(1)
    except Exception as exc:
        console.print(f"[bold red]Configuration error:[/] {exc}")
        sys.exit(1)

    engine = build_engine(database_url)
    from sqlalchemy.orm import sessionmaker

    factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def _run() -> None:
        with session_scope(factory) as session:
            report = await reconcile_via_mcp(session, settings=settings)
        console.print(
            f"[bold green]Reconcile ok[/] equity=${report.account_equity:,.2f} "
            f"cash=${report.account_cash:,.2f} orders={report.orders_seen} "
            f"fills={report.fills_seen} snapshots={report.snapshots_written}"
        )

    asyncio.run(_run())


# ---------------------------------------------------------------------------
# universe
# ---------------------------------------------------------------------------
@universe_app.command("policies")
def universe_policies() -> None:
    """Print every track's universe-eligibility policy."""

    from stockripper.data import DEFAULT_UNIVERSE_POLICIES

    table = Table(title="Universe policies", show_header=True)
    table.add_column("track_id", style="bold cyan")
    table.add_column("min ADV $", justify="right")
    table.add_column("price floor", justify="right")
    table.add_column("cap bands")
    table.add_column("instruments")
    table.add_column("low-vis", justify="center")
    for tid, pol in DEFAULT_UNIVERSE_POLICIES.items():
        table.add_row(
            tid,
            f"{int(pol.min_adv_usd):,}",
            f"{pol.price_floor_usd}",
            ",".join(b.value for b in pol.market_cap_bands_allowed),
            ",".join(i.value for i in pol.instrument_types_allowed),
            "yes" if pol.low_visibility_enabled else "no",
        )
    console.print(table)


@universe_app.command("build")
def universe_build(
    track: str = typer.Option(..., "--track", "-t", help="Track id to build the universe for."),
    limit: int = typer.Option(50, "--limit", "-n", help="Max candidates to print."),
) -> None:
    """Build the candidate universe for ``track`` using live Alpaca data.

    Liquidity/cap data come from Alpaca's snapshot endpoint; news count is
    sourced from the Alpaca News API. SEC EDGAR is not hit unless the
    track's policy enables the recent-catalyst requirement.
    """

    try:
        settings = load_settings()
        settings.assert_paper_only()
    except PaperEndpointError as exc:
        console.print(f"[bold red]Paper-endpoint check failed:[/] {exc}")
        sys.exit(1)
    except Exception as exc:
        console.print(f"[bold red]Configuration error:[/] {exc}")
        sys.exit(1)

    from stockripper.data import UniverseBuilder, UniverseBuildRequest
    from stockripper.data.live import (
        AlpacaAssetsLoader,
        AlpacaSnapshotProvider,
    )

    loader = AlpacaAssetsLoader(settings=settings)
    snapshot_provider = AlpacaSnapshotProvider(settings=settings)
    builder = UniverseBuilder(
        assets_loader=loader,
        snapshot_provider=snapshot_provider,
    )
    request = UniverseBuildRequest(
        track_id=track,
        as_of=dt.date.today(),
        window_id=f"{dt.date.today().isoformat()}-open",
        limit=limit,
    )
    result = builder.build(request)

    table = Table(
        title=f"Universe for {track} ({len(result.candidates)} admitted)",
        show_header=True,
    )
    table.add_column("symbol", style="bold cyan")
    table.add_column("bucket")
    table.add_column("price", justify="right")
    table.add_column("ADV20 $", justify="right")
    table.add_column("cap $", justify="right")
    table.add_column("reasons")
    for cand in result.candidates:
        cap = cand.snapshot.market_cap_usd
        cap_text = f"{int(cap):,}" if cap is not None else "?"
        reasons_text = ", ".join(r.code.value for r in cand.reasons)
        table.add_row(
            cand.symbol,
            cand.bucket,
            f"{cand.snapshot.last_price}",
            f"{int(cand.snapshot.adv_usd_20d):,}",
            cap_text,
            reasons_text,
        )
    console.print(table)
    console.print(f"[dim]diagnostics:[/] {result.diagnostics}")


# ---------------------------------------------------------------------------
# research
# ---------------------------------------------------------------------------
@research_app.command("fundamentals")
def research_fundamentals(symbol: str) -> None:
    """Print derived fundamentals (market cap, revenue, etc) for ``symbol``."""

    from stockripper.data.fundamentals import derive_fundamentals
    from stockripper.data.market_data import MarketDataAdapter
    from stockripper.data.sec_edgar import SecEdgarClient

    md = MarketDataAdapter()
    snap = md.get_snapshot(symbol)
    with SecEdgarClient() as edgar:
        cik = edgar.lookup_cik(symbol)
        if cik is None:
            console.print(f"[bold red]No CIK for[/] {symbol}")
            sys.exit(1)
        facts = edgar.get_company_facts(cik)
    fund = derive_fundamentals(facts, latest_price=snap.last_price)

    table = Table(title=f"Fundamentals for {symbol} ({fund.entity_name})")
    table.add_column("metric", style="bold cyan")
    table.add_column("value")
    table.add_column("as_of")
    table.add_column("source")
    table.add_column("confidence")
    rows = (
        ("shares_outstanding", fund.shares_outstanding),
        ("revenue_ttm", fund.revenue_ttm),
        ("net_income_ttm", fund.net_income_ttm),
        ("total_debt", fund.total_debt),
        ("total_equity", fund.total_equity),
        ("debt_to_equity", fund.debt_to_equity),
        ("market_cap", fund.market_cap),
    )
    for name, val in rows:
        if val is None:
            table.add_row(name, "—", "—", "—", "—")
        else:
            table.add_row(
                name,
                f"{val.value} {val.unit}",
                val.as_of.isoformat(),
                val.source_fact,
                val.confidence,
            )
    console.print(table)


@research_app.command("news")
def research_news(
    symbol: str,
    days: int = typer.Option(7, "--days", "-d", help="Look-back window in days."),
    limit: int = typer.Option(20, "--limit", "-n"),
) -> None:
    """Print recent headlines for ``symbol`` from the Alpaca News API."""

    from stockripper.data.news import NewsAdapter

    adapter = NewsAdapter()
    since = dt.datetime.now(dt.UTC) - dt.timedelta(days=days)
    items = adapter.get_recent_news([symbol], since=since, limit=limit)

    table = Table(title=f"News for {symbol} (last {days}d, {len(items)} items)")
    table.add_column("created_at")
    table.add_column("headline")
    table.add_column("source")
    for item in items:
        table.add_row(
            item.created_at.isoformat(timespec="minutes"),
            item.headline,
            item.source or "",
        )
    console.print(table)


if __name__ == "__main__":  # pragma: no cover - typer dispatch
    app()
