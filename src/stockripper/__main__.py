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
from typing import Any

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
agents_app = typer.Typer(help="Inspect the Phase-3 agent graph (council, judges, baselines).")
prompts_app = typer.Typer(help="Inspect the content-addressed prompt registry.")
kill_app = typer.Typer(help="Global kill-switch (halt every track immediately).")
track_app = typer.Typer(help="Per-track pause control.")
app.add_typer(db_app, name="db")
app.add_typer(tracks_app, name="tracks")
app.add_typer(universe_app, name="universe")
app.add_typer(research_app, name="research")
app.add_typer(agents_app, name="agents")
app.add_typer(prompts_app, name="prompts")
app.add_typer(kill_app, name="kill")
app.add_typer(track_app, name="track")
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


# ---------------------------------------------------------------------------
# agents subcommands
# ---------------------------------------------------------------------------
@agents_app.command("list")
def agents_list(
    track_id: str | None = typer.Option(
        None, "--track", help="Restrict listing to one track id (e.g. balanced, yolo, benchmark)."
    ),
) -> None:
    """List council, adversarial, judge, and baseline agents (optionally per track)."""

    from stockripper.agents.registry import build_registry

    registry = build_registry()
    if track_id is not None and track_id not in registry.bindings:
        console.print(f"[red]unknown track: {track_id}[/red]")
        raise typer.Exit(code=1)

    bindings = (
        [registry.bindings[track_id]] if track_id else list(registry.bindings.values())
    )
    table = Table(title="Agent bindings")
    table.add_column("track")
    table.add_column("kind")
    table.add_column("count")
    table.add_column("agents")
    for b in bindings:
        kind = "llm" if b.is_llm_track else "baseline"
        if b.is_llm_track:
            council_names = ", ".join(b.council_agent_ids)
            table.add_row(
                b.track_id, kind, str(len(b.council_agent_ids)), f"judge={b.judge_id}"
            )
            table.add_row("", "council", "", council_names)
            if b.adversarial_agent_ids:
                table.add_row(
                    "", "adversarial", str(len(b.adversarial_agent_ids)),
                    ", ".join(b.adversarial_agent_ids),
                )
            if b.market_climate_enabled:
                table.add_row("", "climate", "1", registry.market_climate.agent_id)
        else:
            table.add_row(b.track_id, kind, "1", b.judge_id)
    console.print(table)


@agents_app.command("describe")
def agents_describe(agent_id: str = typer.Argument(...)) -> None:
    """Print metadata for one agent (council, adversarial, judge, baseline)."""

    from stockripper.agents.council import COUNCIL
    from stockripper.agents.judges import JUDGES
    from stockripper.agents.registry import build_registry

    registry = build_registry()

    for spec in COUNCIL:
        if spec.agent_id == agent_id:
            console.print(f"[bold]Council agent[/bold]: {spec.agent_id}")
            console.print(f"  label: {spec.label}")
            console.print(f"  family: {spec.family}")
            console.print(f"  default_horizon_days: {spec.default_horizon_days}")
            console.print(
                "  allowed_actions: " + ", ".join(a.value for a in spec.allowed_actions)
            )
            console.print(
                "  allowed_instruments: "
                + ", ".join(i.value for i in spec.allowed_instruments)
            )
            console.print(f"  philosophy: {spec.philosophy}")
            return

    for jspec in JUDGES:
        if jspec.judge_id == agent_id:
            console.print(f"[bold]Judge[/bold]: {jspec.judge_id}")
            console.print(f"  track: {jspec.track_id}")
            console.print(f"  objective: {jspec.objective_label}")
            console.print(f"  prompt_template: {jspec.prompt_template_id}")
            return

    if agent_id in registry.baselines:
        baseline = registry.baselines[agent_id]
        console.print(f"[bold]Baseline[/bold]: {baseline.agent_id}")
        console.print(f"  version: {baseline.agent_version}")
        console.print(f"  requires_llm: {baseline.requires_llm}")
        return

    for special in (
        registry.market_climate,
        registry.skeptic,
        registry.risk_manager,
        registry.prompt_injection,
    ):
        if special.agent_id == agent_id:
            console.print(f"[bold]Adversarial / utility[/bold]: {special.agent_id}")
            console.print(f"  version: {special.agent_version}")
            requires_llm = getattr(special, "requires_llm", None)
            if requires_llm is not None:
                console.print(f"  requires_llm: {requires_llm}")
            return

    console.print(f"[red]unknown agent_id: {agent_id}[/red]")
    raise typer.Exit(code=1)


# ---------------------------------------------------------------------------
# prompts subcommands
# ---------------------------------------------------------------------------
@prompts_app.command("list")
def prompts_list() -> None:
    """List every registered prompt template with content hash and length."""

    # Eagerly instantiate council + judges so lazy registrations land in PROMPTS.
    from stockripper.agents.prompts import PROMPTS
    from stockripper.agents.registry import build_registry

    build_registry()
    templates = sorted(PROMPTS.all_templates(), key=lambda t: t.template_id)
    table = Table(title=f"Prompt templates ({len(templates)})")
    table.add_column("template_id")
    table.add_column("version")
    table.add_column("content_hash", overflow="fold")
    table.add_column("chars", justify="right")
    for t in templates:
        table.add_row(t.template_id, t.version, t.content_hash[:16] + "…", str(len(t.body)))
    console.print(table)


@prompts_app.command("show")
def prompts_show(
    template_id: str = typer.Argument(...),
    with_preamble: bool = typer.Option(
        False, "--with-preamble/--no-preamble",
        help="Include the universal policy preamble in the rendered output.",
    ),
) -> None:
    """Print one prompt template (body or fully-rendered text)."""

    from stockripper.agents.prompts import PROMPTS
    from stockripper.agents.registry import build_registry

    build_registry()
    if template_id not in PROMPTS:
        console.print(f"[red]unknown template_id: {template_id}[/red]")
        raise typer.Exit(code=1)
    tmpl = PROMPTS.get(template_id)
    console.print(f"[bold]template_id[/bold]: {tmpl.template_id}")
    console.print(f"[bold]version[/bold]: {tmpl.version}")
    console.print(f"[bold]content_hash[/bold]: {tmpl.content_hash}")
    console.print(f"[bold]rendered_content_hash[/bold]: {tmpl.rendered_content_hash}")
    console.print("---")
    console.print(tmpl.render(include_preamble=with_preamble))


# ---------------------------------------------------------------------------
# agents run-track (Phase 4 minimal-slice orchestrator)
# ---------------------------------------------------------------------------
@agents_app.command("run-track")
def agents_run_track(
    track_id: str = typer.Argument(..., help="Track to run (e.g. balanced, yolo, benchmark)."),
    symbol: list[str] = typer.Option(  # noqa: B008 - typer pattern
        ["SPY"], "--symbol", "-s",
        help="One or more symbols. The orchestrator runs once per symbol.",
    ),
    fake: bool = typer.Option(
        True, "--fake/--no-fake",
        help="Use the offline CannedCouncilLLM. Pass --no-fake to call real OpenAI.",
    ),
    seed: int | None = typer.Option(
        None, "--seed", help="Optional rng_seed propagated to every agent run."
    ),
) -> None:
    """Run one (track, symbol) collaborative cycle and print the judge's plan.

    Default mode uses the canned offline LLM so council + skeptic + risk +
    judge all execute end-to-end without network. Pass ``--no-fake`` to use
    real OpenAI; the judge uses ``OPENAI_MODEL_JUDGE`` and every other agent
    uses ``OPENAI_MODEL_DEFAULT``.
    """

    from stockripper.agents.canned_llm import CannedCouncilLLM
    from stockripper.agents.demo import build_demo_packet
    from stockripper.agents.llm import LLMClient, OpenAIStructuredClient
    from stockripper.agents.orchestrator import run_track
    from stockripper.agents.registry import build_registry

    registry = build_registry()
    if track_id not in registry.bindings:
        console.print(f"[red]unknown track: {track_id}[/red]")
        raise typer.Exit(code=1)

    llm: LLMClient | None = None
    binding = registry.bindings[track_id]
    if fake:
        llm = CannedCouncilLLM()
        console.print("[yellow]offline mode: using CannedCouncilLLM[/yellow]")
    elif binding.is_llm_track:
        settings = load_settings()
        llm = OpenAIStructuredClient(
            api_key=settings.openai_api_key.get_secret_value(),
            default_model=settings.openai_model_default,
        )
        for judge in registry.judges.values():
            judge.model_id_override = settings.openai_model_judge
        console.print(
            f"[green]live mode: agents={settings.openai_model_default} "
            f"judge={settings.openai_model_judge}[/green]"
        )

    async def _run_all() -> None:
        for sym in symbol:
            packet = build_demo_packet(symbol=sym, track_id=track_id)
            result = await run_track(
                registry=registry,
                track_id=track_id,
                packet=packet,
                llm=llm,
                rng_seed=seed,
            )
            _print_track_run_result(result)

    asyncio.run(_run_all())


def _print_track_run_result(result: Any) -> None:
    from stockripper.agents.schemas import (
        AgentRecommendation,
        AgentRunStatus,
    )

    console.print()
    console.print(
        f"[bold cyan]Track run[/bold cyan] track={result.track_id} "
        f"symbol={result.packet.symbol} window={result.window_id} "
        f"run_id={result.run_id}"
    )

    if result.market_climate is not None:
        mc = result.market_climate
        console.print(
            f"  [blue]market_climate[/blue]: regime={mc.regime.value} "
            f"breadth={mc.breadth_score} volatility={mc.volatility_score}"
        )

    council_table = Table(title=f"Council ({len(result.council_runs)} runs)")
    council_table.add_column("agent_id")
    council_table.add_column("status")
    council_table.add_column("action")
    council_table.add_column("instrument")
    council_table.add_column("conviction", justify="right")
    council_table.add_column("notes", overflow="fold")
    for run in result.council_runs:
        if (
            run.status == AgentRunStatus.OK
            and isinstance(run.output, AgentRecommendation)
        ):
            rec = run.output
            council_table.add_row(
                run.agent_id,
                run.status.value,
                rec.action.value,
                rec.instrument.value,
                str(rec.conviction),
                rec.thesis[:90],
            )
        else:
            council_table.add_row(
                run.agent_id, run.status.value, "-", "-", "-",
                (run.quarantine_reason or "")[:90],
            )
    console.print(council_table)

    if result.skeptic_run is not None:
        sk = result.skeptic_run
        if result.skeptic_report is not None:
            console.print(
                f"  [magenta]skeptic[/magenta]: status={sk.status.value} "
                f"critiques={len(result.skeptic_report.critiques)}"
            )
        else:
            console.print(
                f"  [red]skeptic quarantined[/red]: {sk.quarantine_reason}"
            )

    if result.risk_run is not None:
        rm = result.risk_run
        if result.risk_report is not None:
            console.print(
                f"  [magenta]risk_manager[/magenta]: status={rm.status.value} "
                f"assessments={len(result.risk_report.assessments)} "
                f"portfolio_flags={len(result.risk_report.portfolio_level_flags)}"
            )
        else:
            console.print(
                f"  [red]risk_manager quarantined[/red]: {rm.quarantine_reason}"
            )

    judge = result.judge_run
    decision = result.judge_decision
    if decision is None:
        console.print(
            f"  [red]judge quarantined[/red]: {judge.quarantine_reason}"
        )
        return
    plan = decision.plan
    console.print(
        f"  [bold green]judge[/bold green] {plan.judge_agent_id} "
        f"posture={plan.portfolio_posture.value} "
        f"objective={plan.objective_label} items={len(plan.items)}"
    )
    console.print(f"  rationale: {plan.rationale[:180]}")
    if plan.items:
        items_table = Table(title=f"Action items ({len(plan.items)})")
        items_table.add_column("symbol")
        items_table.add_column("side")
        items_table.add_column("instrument")
        items_table.add_column("size", justify="right")
        items_table.add_column("rationale", overflow="fold")
        for item in plan.items:
            size_text = (
                f"${item.target_notional_usd}"
                if item.target_notional_usd is not None
                else f"{item.target_pct_equity}*equity"
                if item.target_pct_equity is not None
                else "-"
            )
            items_table.add_row(
                item.symbol,
                item.side.value,
                item.instrument.value,
                size_text,
                item.rationale[:80],
            )
        console.print(items_table)

    if result.quarantined_runs:
        console.print(
            f"  [yellow]quarantined runs: {len(result.quarantined_runs)}[/yellow]"
        )


if __name__ == "__main__":  # pragma: no cover - typer dispatch
    app()


# ---------------------------------------------------------------------------
# Kill switch (global halt)
# ---------------------------------------------------------------------------
def _kill_switch_factory(database_url: str | None) -> Any:
    from sqlalchemy.orm import sessionmaker

    engine = build_engine(database_url)
    return sessionmaker(engine, expire_on_commit=False, autoflush=False)


@kill_app.command("on")
def kill_on(
    reason: str = typer.Argument(..., help="Why are we halting?"),
    by: str | None = typer.Option(None, "--by", help="Operator id or username."),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Engage the global kill-switch. Every track aborts on its next window."""

    from stockripper.db.repository import Repository

    factory = _kill_switch_factory(database_url)
    with session_scope(factory) as session:
        repo = Repository(session)
        state = repo.engage_kill_switch(reason=reason, engaged_by=by)
        console.print(
            f"[bold red]KILL SWITCH ENGAGED[/] reason={state.reason!r} by={state.engaged_by!r} "
            f"at={state.engaged_at!s}"
        )


@kill_app.command("off")
def kill_off(
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Release the global kill-switch."""

    from stockripper.db.repository import Repository

    factory = _kill_switch_factory(database_url)
    with session_scope(factory) as session:
        repo = Repository(session)
        state = repo.release_kill_switch()
        console.print(
            f"[bold green]kill switch released[/] updated_at={state.updated_at!s}"
        )


@kill_app.command("status")
def kill_status(
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Print kill-switch status."""

    from stockripper.db.repository import Repository

    factory = _kill_switch_factory(database_url)
    with session_scope(factory) as session:
        repo = Repository(session)
        state = repo.get_kill_switch()
        if state.engaged:
            console.print(
                f"[bold red]ENGAGED[/] reason={state.reason!r} by={state.engaged_by!r} "
                f"at={state.engaged_at!s}"
            )
        else:
            console.print(
                f"[bold green]not engaged[/] last_updated={state.updated_at!s}"
            )


# ---------------------------------------------------------------------------
# Per-track pause control
# ---------------------------------------------------------------------------
@track_app.command("pause")
def track_pause(
    track_id: str = typer.Argument(..., help="track_id to pause."),
    reason: str = typer.Option("manual", "--reason", help="Why is this track paused?"),
    by: str | None = typer.Option(None, "--by", help="Operator id or username."),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Pause one track; future windows skip it until resumed."""

    from stockripper.db.repository import Repository

    factory = _kill_switch_factory(database_url)
    with session_scope(factory) as session:
        repo = Repository(session)
        state = repo.pause_track(track_id=track_id, reason=reason, paused_by=by)
        console.print(
            f"[yellow]paused[/] track={state.track_id} reason={state.reason!r} "
            f"by={state.paused_by!r} at={state.paused_at!s}"
        )


@track_app.command("resume")
def track_resume(
    track_id: str = typer.Argument(..., help="track_id to resume."),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Resume a previously-paused track."""

    from stockripper.db.repository import Repository

    factory = _kill_switch_factory(database_url)
    with session_scope(factory) as session:
        repo = Repository(session)
        state = repo.resume_track(track_id=track_id)
        console.print(
            f"[green]resumed[/] track={state.track_id} updated_at={state.updated_at!s}"
        )


@track_app.command("status")
def track_status(
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Print all known pause state rows."""

    from stockripper.db.repository import Repository

    factory = _kill_switch_factory(database_url)
    with session_scope(factory) as session:
        repo = Repository(session)
        rows = repo.list_track_pause_states()
    if not rows:
        console.print("[dim]No pause state rows recorded.[/dim]")
        return
    table = Table(title="Track pause state")
    table.add_column("track_id", style="bold cyan")
    table.add_column("paused", justify="center")
    table.add_column("reason")
    table.add_column("by")
    table.add_column("paused_at")
    table.add_column("updated_at")
    for row in rows:
        table.add_row(
            row.track_id,
            "[red]✓[/]" if row.paused else "[green]✗[/]",
            (row.reason or "") if row.paused else "",
            row.paused_by or "",
            str(row.paused_at) if row.paused_at else "",
            str(row.updated_at),
        )
    console.print(table)


# ---------------------------------------------------------------------------
# Multi-track window runner
# ---------------------------------------------------------------------------
@agents_app.command("run-window")
def agents_run_window(
    track: list[str] = typer.Option(  # noqa: B008 - typer pattern
        [], "--track", "-t",
        help="Restrict to specific track_ids. Defaults to every enabled track.",
    ),
    symbol: list[str] = typer.Option(  # noqa: B008 - typer pattern
        ["SPY"], "--symbol", "-s",
        help="One or more symbols. The runner fans out (track x symbol) in parallel.",
    ),
    window_label: str = typer.Option(
        "adhoc", "--window-label", help="Window label (e.g. opening, midday, close).",
    ),
    fake: bool = typer.Option(
        True, "--fake/--no-fake",
        help="Use the offline CannedCouncilLLM. Pass --no-fake to call real OpenAI.",
    ),
    persist: bool = typer.Option(
        True, "--persist/--no-persist",
        help="When --persist, write run/track/agent rows to the DB (default).",
    ),
    execute: bool = typer.Option(
        False, "--execute/--no-execute",
        help=(
            "When --execute, submit approved actions through the execution "
            "adapter (defaults to the mock broker; pass --alpaca for paper "
            "submission). Requires --persist."
        ),
    ),
    alpaca: bool = typer.Option(
        False, "--alpaca/--mock",
        help=(
            "Broker selector for --execute. --alpaca submits paper orders "
            "via alpaca-py; --mock (default) uses the in-process mock that "
            "synthesizes immediate fills."
        ),
    ),
    seed: int | None = typer.Option(
        None, "--seed", help="rng_seed propagated to every agent run.",
    ),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Run every (track, symbol) pair in parallel via the Strategy Tracks Manager.

    Honors the global kill-switch and per-track pause state. Without
    ``--persist`` no DB writes happen — useful for offline demos. With
    ``--execute`` the per-track risk gate runs and approved orders are
    submitted through the chosen broker (mock by default).
    """

    from sqlalchemy.orm import sessionmaker

    from stockripper.agents.llm import LLMClient, OpenAIStructuredClient
    from stockripper.agents.registry import build_registry
    from stockripper.agents.window_runner import run_window

    registry = build_registry()
    track_ids = (
        tuple(track) if track
        else tuple(t.track_id for t in DEFAULT_TRACKS if t.enabled)
    )
    symbols = tuple(s.upper() for s in symbol)

    factory: Any = None
    if persist:
        engine = build_engine(database_url)
        factory = sessionmaker(engine, expire_on_commit=False, autoflush=False)

    if execute and not persist:
        console.print(
            "[bold red]--execute requires --persist (no audit row, no submit).[/]"
        )
        raise typer.Exit(code=2)

    execution_adapter: Any = None
    if execute:
        from stockripper.execution.adapter import (
            AlpacaPaperBrokerClient,
            ExecutionAdapter,
            MockBrokerClient,
        )

        broker: Any
        settings: StockripperSettings | None
        if alpaca:
            settings = load_settings()
            settings.assert_paper_only()
            broker = AlpacaPaperBrokerClient(settings)
            console.print(
                f"[bold yellow]execute mode: alpaca paper broker "
                f"(endpoint={settings.alpaca_base_url})[/]"
            )
        else:
            settings = None
            broker = MockBrokerClient()
            console.print("[yellow]execute mode: mock broker (synthetic fills)[/]")
        execution_adapter = ExecutionAdapter(
            session_factory=factory,
            broker=broker,
            window_id=window_label,
            settings=settings,
        )

    if fake:
        console.print("[yellow]offline mode: using CannedCouncilLLM per coroutine[/yellow]")
        llm_factory = None
    else:
        settings = load_settings()
        api_key = settings.openai_api_key.get_secret_value()
        default_model = settings.openai_model_default
        judge_model = settings.openai_model_judge
        for judge in registry.judges.values():
            judge.model_id_override = judge_model
        console.print(
            f"[green]live mode: agents={default_model} judge={judge_model}[/green]"
        )

        def llm_factory(_packet: Any) -> LLMClient | None:
            return OpenAIStructuredClient(
                api_key=api_key, default_model=default_model,
            )

    async def _run_all() -> None:
        result = await run_window(
            registry=registry,
            track_ids=track_ids,
            symbols=symbols,
            window_label=window_label,
            rng_seed=seed,
            persist=persist,
            session_factory=factory,
            llm_factory=llm_factory,
            execution_adapter=execution_adapter,
        )
        _print_window_result(result)

    asyncio.run(_run_all())


def _print_window_result(result: Any) -> None:
    console.print()
    console.print(
        f"[bold cyan]Window run[/bold cyan] run_id={result.run_id} "
        f"window={result.window_label} status=[bold]{result.status}[/bold] "
        f"day={result.trading_day} "
        f"duration={(result.completed_at - result.started_at).total_seconds():.2f}s"
    )
    table = Table(title=f"Per-(track,symbol) outcomes ({len(result.outcomes)})")
    table.add_column("track_id", style="bold cyan")
    table.add_column("symbol")
    table.add_column("status", justify="center")
    table.add_column("decision_items", justify="right")
    table.add_column("notes", overflow="fold")
    for o in result.outcomes:
        if o.result is not None and o.result.judge_decision is not None:
            items = str(len(o.result.judge_decision.plan.items))
            note = o.result.judge_decision.plan.rationale[:90]
        elif o.result is not None:
            items = "-"
            note = (o.result.judge_run.quarantine_reason or "")[:90]
        else:
            items = "-"
            note = (o.reason or o.error or "")[:90]
        status_text = {
            "ok": "[green]ok[/]",
            "partial": "[yellow]partial[/]",
            "skipped_paused": "[blue]skipped[/]",
            "aborted_kill": "[bold red]kill[/]",
            "failed": "[red]failed[/]",
        }.get(o.status, o.status)
        table.add_row(o.track_id, o.symbol, status_text, items, note)
    console.print(table)

    submissions = getattr(result, "submissions", ()) or ()
    if submissions:
        sub_table = Table(title=f"Execution submissions ({len(submissions)})")
        sub_table.add_column("track_id", style="bold cyan")
        sub_table.add_column("symbol")
        sub_table.add_column("status", justify="center")
        sub_table.add_column("client_order_id", overflow="fold")
        sub_table.add_column("reason", overflow="fold")
        sub_status_style = {
            "submitted": "[green]submitted[/]",
            "duplicate": "[blue]duplicate[/]",
            "rejected_floor": "[bold red]floor[/]",
            "rejected_risk": "[red]risk[/]",
            "error": "[bold red]error[/]",
        }
        for s in submissions:
            sub_table.add_row(
                s.track_id,
                s.symbol,
                sub_status_style.get(s.status.value, s.status.value),
                s.client_order_id or "-",
                (s.reason or "")[:90],
            )
        console.print(sub_table)




# ---------------------------------------------------------------------------
# Phase 6 — scoring + dashboard
# ---------------------------------------------------------------------------
scoring_app = typer.Typer(
    help="Score recommendations, compute leaderboards, and compute judge regret.",
)
dashboard_app = typer.Typer(help="Live dashboard for orchestrator activity.")
app.add_typer(scoring_app, name="scoring")
app.add_typer(dashboard_app, name="dashboard")


def _scoring_session_factory(database_url: str | None) -> Any:
    from sqlalchemy.orm import sessionmaker

    engine = build_engine(database_url)
    return sessionmaker(engine, expire_on_commit=False, autoflush=False)


@scoring_app.command("score")
def scoring_score(
    run_id: str = typer.Option(..., "--run-id", help="Window run_id to score."),
    as_of: str = typer.Option(
        ..., "--as-of", help="As-of date for the AgentScore rows (ISO YYYY-MM-DD).",
    ),
    horizon_days: int = typer.Option(
        5, "--horizon-days",
        help="Default horizon used by the stub price provider for tests.",
    ),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Score every recommendation in ``run_id`` using a stub price provider.

    Live runs should plug in an Alpaca-backed PriceProvider in code;
    this command intentionally uses the stub so it is safe to invoke
    offline against any database.
    """

    from decimal import Decimal

    from stockripper.db.engine import session_scope as _session_scope
    from stockripper.scoring.reward import (
        StaticPriceProvider,
        score_recommendations_for_window,
    )

    as_of_date = dt.date.fromisoformat(as_of)
    factory = _scoring_session_factory(database_url)
    table: dict[tuple[str, dt.date, int], Decimal] = {}
    with _session_scope(factory) as session:
        repo = Repository(session)
        recs = repo.list_recommendations(run_id=run_id)
        for rec in recs:
            key = (rec.symbol.upper(), rec.created_at.date(), int(rec.time_horizon_days))
            table.setdefault(key, Decimal("0"))
            bench_key = ("SPY", rec.created_at.date(), int(rec.time_horizon_days))
            table.setdefault(bench_key, Decimal("0"))
        _ = horizon_days
        provider = StaticPriceProvider(table=table)
        rows = score_recommendations_for_window(
            session=session,
            run_id=run_id,
            as_of_date=as_of_date,
            price_provider=provider,
        )
    if not rows:
        console.print("[yellow]No recommendations scored.[/]")
        return
    table_out = Table(title=f"AgentScore rows for run {run_id}")
    table_out.add_column("agent_id", style="bold cyan")
    table_out.add_column("track_id")
    table_out.add_column("reward", justify="right")
    table_out.add_column("n", justify="right")
    for agent_id, track_id, reward, n in rows:
        table_out.add_row(agent_id, track_id, f"{reward:+.6f}", str(n))
    console.print(table_out)


@scoring_app.command("leaderboard")
def scoring_leaderboard(
    window_start: str = typer.Option(..., "--window-start"),
    window_end: str = typer.Option(..., "--window-end"),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Compute and persist the head-to-head per-track leaderboard."""

    from stockripper.db.engine import session_scope as _session_scope
    from stockripper.scoring.leaderboard import (
        compute_leaderboard,
        persist_leaderboard,
    )

    ws = dt.date.fromisoformat(window_start)
    we = dt.date.fromisoformat(window_end)
    factory = _scoring_session_factory(database_url)
    with _session_scope(factory) as session:
        metrics = compute_leaderboard(
            session=session, window_start=ws, window_end=we,
        )
        persist_leaderboard(
            session=session, window_start=ws, window_end=we, metrics=metrics,
        )
    if not metrics:
        console.print("[yellow]No snapshots in window; nothing to rank.[/]")
        return
    out = Table(title=f"Track leaderboard {ws} → {we}")
    out.add_column("track_id", style="bold cyan")
    out.add_column("cum_ret", justify="right")
    out.add_column("sharpe", justify="right")
    out.add_column("max_dd", justify="right")
    out.add_column("win_rate", justify="right")
    for m in metrics:
        out.add_row(
            m.track_id,
            "-" if m.cumulative_return_pct is None else f"{m.cumulative_return_pct:+.6f}",
            "-" if m.sharpe is None else f"{m.sharpe:+.4f}",
            "-" if m.max_drawdown_pct is None else f"{m.max_drawdown_pct:.6f}",
            "-" if m.win_rate is None else f"{m.win_rate:.4f}",
        )
    console.print(out)


@scoring_app.command("regret")
def scoring_regret(
    run_id: str = typer.Option(..., "--run-id"),
    track_id: str = typer.Option(..., "--track-id"),
    as_of: str = typer.Option(..., "--as-of"),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Compute and persist judge regret for one (run, track, date)."""

    from decimal import Decimal

    from stockripper.db.engine import session_scope as _session_scope
    from stockripper.scoring.judge_regret import (
        compute_judge_regret_for_track,
        persist_judge_regret_for_track,
    )

    as_of_date = dt.date.fromisoformat(as_of)
    factory = _scoring_session_factory(database_url)
    with _session_scope(factory) as session:
        repo = Repository(session)
        recs = repo.list_recommendations(run_id=run_id, track_id=track_id)
        rewards: dict[str, Decimal] = {
            r.recommendation_id: Decimal("0") for r in recs
        }
        report = compute_judge_regret_for_track(
            session=session,
            run_id=run_id,
            track_id=track_id,
            as_of_date=as_of_date,
            rewards=rewards,
        )
        if report is None:
            console.print("[yellow]No judge decision or recommendations for that key.[/]")
            return
        persist_judge_regret_for_track(session=session, report=report)
    console.print(
        f"[bold cyan]Regret[/bold cyan] judge={report.judge_agent_id} "
        f"track={report.track_id} as_of={report.as_of_date} "
        f"selected={report.selected_reward:+.6f} "
        f"best_alt={report.best_alternative_reward:+.6f} "
        f"regret={report.regret:+.6f} n={report.observation_count}"
    )


@dashboard_app.command("serve")
def dashboard_serve(
    host: str = typer.Option("127.0.0.1", "--host"),
    port: int = typer.Option(8765, "--port"),
    database_url: str | None = typer.Option(None, "--database-url"),
) -> None:
    """Serve the read-only dashboard backed by the ledger DB."""

    import uvicorn

    from stockripper.dashboard.app import build_app

    factory = _scoring_session_factory(database_url)
    app_instance = build_app(session_factory=factory)
    console.print(
        f"[bold green]StockRipper dashboard[/bold green] http://{host}:{port}"
    )
    uvicorn.run(app_instance, host=host, port=port, log_level="info")


# Repository import is deferred until after the new commands above so that
# circular-import edge cases stay impossible.
from stockripper.db.repository import Repository  # noqa: E402

