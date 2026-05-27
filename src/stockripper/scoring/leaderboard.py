"""Per-track leaderboard computation (spec §8.3 / §25 Phase 6)."""

from __future__ import annotations

import datetime as dt
import hashlib
import itertools
import math
from collections.abc import Sequence
from dataclasses import dataclass
from decimal import Decimal

from sqlalchemy import select
from sqlalchemy.orm import Session

from stockripper.db.models import Fill, Order, TrackSnapshot
from stockripper.db.repository import Repository

_TRADING_DAYS_PER_YEAR = Decimal("252")


@dataclass(frozen=True)
class LeaderboardMetrics:
    track_id: str
    cumulative_return_pct: Decimal | None
    sharpe: Decimal | None
    sortino: Decimal | None
    calmar: Decimal | None
    max_drawdown_pct: Decimal | None
    win_rate: Decimal | None
    turnover: Decimal | None


def _leaderboard_id(
    *, window_start: dt.date, window_end: dt.date, track_id: str,
) -> str:
    body = (
        f"{window_start.isoformat()}\x00{window_end.isoformat()}\x00{track_id}"
    )
    return "lb_" + hashlib.sha256(body.encode("utf-8")).hexdigest()[:24]


def _daily_snapshots(
    snaps: Sequence[TrackSnapshot],
) -> list[TrackSnapshot]:
    """Collapse to one row per date — the latest snapshot of that day."""

    by_day: dict[dt.date, TrackSnapshot] = {}
    for s in snaps:
        existing = by_day.get(s.captured_at.date())
        if existing is None or s.captured_at > existing.captured_at:
            by_day[s.captured_at.date()] = s
    return [by_day[k] for k in sorted(by_day.keys())]


def _daily_returns(snaps: Sequence[TrackSnapshot]) -> list[Decimal]:
    rets: list[Decimal] = []
    for prev, curr in itertools.pairwise(snaps):
        if prev.equity == 0:
            continue
        rets.append((curr.equity - prev.equity) / prev.equity)
    return rets


def _max_drawdown(snaps: Sequence[TrackSnapshot]) -> Decimal:
    peak = Decimal("0")
    worst = Decimal("0")
    for s in snaps:
        if s.equity > peak:
            peak = s.equity
        if peak > 0:
            dd = (peak - s.equity) / peak
            if dd > worst:
                worst = dd
    return worst


def _std(values: Sequence[Decimal]) -> Decimal:
    if len(values) < 2:
        return Decimal("0")
    mean = sum(values, start=Decimal("0")) / Decimal(len(values))
    sq = sum(((v - mean) ** 2 for v in values), start=Decimal("0"))
    var = sq / Decimal(len(values) - 1)
    return Decimal(str(math.sqrt(float(var))))


def _downside_std(values: Sequence[Decimal]) -> Decimal:
    downs = [v for v in values if v < 0]
    if len(downs) < 1:
        return Decimal("0")
    return _std(downs)


def _annualization_factor() -> Decimal:
    return Decimal(str(math.sqrt(float(_TRADING_DAYS_PER_YEAR))))


def _q(value: Decimal | None) -> Decimal | None:
    if value is None:
        return None
    return value.quantize(Decimal("0.000001"))


def _compute_metrics(
    *,
    track_id: str,
    snaps: Sequence[TrackSnapshot],
    turnover: Decimal | None,
) -> LeaderboardMetrics:
    daily = _daily_snapshots(snaps)
    if not daily:
        return LeaderboardMetrics(
            track_id=track_id,
            cumulative_return_pct=None,
            sharpe=None,
            sortino=None,
            calmar=None,
            max_drawdown_pct=None,
            win_rate=None,
            turnover=turnover,
        )
    first, last = daily[0], daily[-1]
    cum: Decimal | None = None
    if first.equity > 0:
        cum = (last.equity - first.equity) / first.equity

    rets = _daily_returns(daily)
    sharpe: Decimal | None = None
    sortino: Decimal | None = None
    win_rate: Decimal | None = None
    if rets:
        win_rate = (
            Decimal(sum(1 for r in rets if r > 0)) / Decimal(len(rets))
        )
    if len(rets) >= 2:
        mean = sum(rets, start=Decimal("0")) / Decimal(len(rets))
        sd = _std(rets)
        dsd = _downside_std(rets)
        ann = _annualization_factor()
        if sd != 0:
            sharpe = (mean / sd) * ann
        if dsd != 0:
            sortino = (mean / dsd) * ann

    mdd = _max_drawdown(daily)
    calmar: Decimal | None = None
    if mdd > 0 and cum is not None:
        calmar = cum / mdd

    return LeaderboardMetrics(
        track_id=track_id,
        cumulative_return_pct=_q(cum),
        sharpe=_q(sharpe),
        sortino=_q(sortino),
        calmar=_q(calmar),
        max_drawdown_pct=_q(mdd) if mdd != 0 else Decimal("0"),
        win_rate=_q(win_rate),
        turnover=_q(turnover),
    )


def _track_turnover(
    *, session: Session, track_id: str,
    window_start: dt.date, window_end: dt.date,
) -> Decimal | None:
    """Sum |notional| for fills inside [window_start, window_end]."""

    end_inclusive = dt.datetime.combine(window_end, dt.time.max, tzinfo=dt.UTC)
    start_inclusive = dt.datetime.combine(window_start, dt.time.min, tzinfo=dt.UTC)
    stmt = (
        select(Fill, Order)
        .join(Order, Fill.local_order_id == Order.local_order_id)
        .where(Order.track_id == track_id)
        .where(Fill.filled_at >= start_inclusive)
        .where(Fill.filled_at <= end_inclusive)
    )
    rows = session.execute(stmt).all()
    if not rows:
        return None
    total = Decimal("0")
    for fill, _order in rows:
        total += abs(fill.filled_qty * fill.filled_avg_price)
    return total


def compute_leaderboard(
    *,
    session: Session,
    window_start: dt.date,
    window_end: dt.date,
    track_ids: Sequence[str] | None = None,
) -> list[LeaderboardMetrics]:
    """Compute per-track leaderboard metrics for the window."""

    repo = Repository(session)
    tracks = repo.list_strategy_tracks()
    if track_ids is not None:
        wanted = set(track_ids)
        tracks = [t for t in tracks if t.track_id in wanted]
    end_inclusive = dt.datetime.combine(window_end, dt.time.max, tzinfo=dt.UTC)
    start_inclusive = dt.datetime.combine(window_start, dt.time.min, tzinfo=dt.UTC)
    out: list[LeaderboardMetrics] = []
    for track in tracks:
        snap_stmt = (
            select(TrackSnapshot)
            .where(TrackSnapshot.track_id == track.track_id)
            .where(TrackSnapshot.captured_at >= start_inclusive)
            .where(TrackSnapshot.captured_at <= end_inclusive)
            .order_by(TrackSnapshot.captured_at.asc())
        )
        snaps = list(session.execute(snap_stmt).scalars())
        if not snaps:
            continue
        turnover_total = _track_turnover(
            session=session,
            track_id=track.track_id,
            window_start=window_start,
            window_end=window_end,
        )
        turnover: Decimal | None = None
        if turnover_total is not None:
            starting = snaps[0].equity or track.starting_equity_usd
            if starting > 0:
                turnover = turnover_total / starting
        metrics = _compute_metrics(
            track_id=track.track_id, snaps=snaps, turnover=turnover,
        )
        out.append(metrics)
    return out


def persist_leaderboard(
    *,
    session: Session,
    window_start: dt.date,
    window_end: dt.date,
    metrics: Sequence[LeaderboardMetrics],
) -> None:
    """Persist computed metrics, ranking by ``cumulative_return_pct``."""

    repo = Repository(session)
    ranked = sorted(
        metrics,
        key=lambda m: (
            m.cumulative_return_pct is None,
            -(m.cumulative_return_pct or Decimal("0")),
        ),
    )
    for i, m in enumerate(ranked, start=1):
        repo.upsert_leaderboard_entry(
            leaderboard_id=_leaderboard_id(
                window_start=window_start,
                window_end=window_end,
                track_id=m.track_id,
            ),
            window_start=window_start,
            window_end=window_end,
            track_id=m.track_id,
            cumulative_return_pct=m.cumulative_return_pct,
            sharpe=m.sharpe,
            sortino=m.sortino,
            calmar=m.calmar,
            max_drawdown_pct=m.max_drawdown_pct,
            win_rate=m.win_rate,
            turnover=m.turnover,
            rank=i,
        )


__all__ = (
    "LeaderboardMetrics",
    "compute_leaderboard",
    "persist_leaderboard",
)
