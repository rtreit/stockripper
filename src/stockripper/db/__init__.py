"""Database layer: SQLAlchemy 2.x models, engine factory, repository."""

from stockripper.db.engine import build_engine, build_session_factory, session_scope
from stockripper.db.models import (
    AgentRun,
    AgentScore,
    Base,
    DecisionAction,
    Fill,
    JudgeDecision,
    JudgeRegretEntry,
    KillSwitchState,
    Order,
    Recommendation,
    RiskPolicy,
    Run,
    StrategyTrack,
    TrackLeaderboardEntry,
    TrackPauseState,
    TrackRun,
    TrackSnapshot,
)
from stockripper.db.repository import Repository

__all__ = (
    "AgentRun",
    "AgentScore",
    "Base",
    "DecisionAction",
    "Fill",
    "JudgeDecision",
    "JudgeRegretEntry",
    "KillSwitchState",
    "Order",
    "Recommendation",
    "Repository",
    "RiskPolicy",
    "Run",
    "StrategyTrack",
    "TrackLeaderboardEntry",
    "TrackPauseState",
    "TrackRun",
    "TrackSnapshot",
    "build_engine",
    "build_session_factory",
    "session_scope",
)

