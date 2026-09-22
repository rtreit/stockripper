using System.Collections.Immutable;
using Stockripper.Domain.Common;

namespace Stockripper.Domain.Audit;

public enum AuditEventType
{
    GOAL_DRAFT_CREATED,
    GOAL_DRAFT_UPDATED,
    GOAL_VERSION_PUBLISHED,
    GOAL_ACTIVATED,
    GOAL_ACTIVATION_STATE_CHANGED,
    ACCOUNT_ALLOCATION_LIMIT_CONFIGURED,
    BROKER_CONNECTION_VERIFIED,
    ACCOUNT_ALLOCATION_RESERVED,
    ACCOUNT_ALLOCATION_RELEASED,
    SESSION_CREATED,
    SESSION_STATE_CHANGED,
    SESSION_PREFLIGHT_COMPLETED,
    DECISION_RECORDED,
    RISK_EVALUATED,
    EXECUTION_INTENT_CREATED,
    EXECUTION_INTENT_STATE_CHANGED,
    REVIEW_RECORDED,
    KILL_SWITCH_CONFIGURED,
    KILL_SWITCH_STATE_CHANGED,
    RECONCILIATION_STARTED,
    RECONCILIATION_COMPLETED,
    EXPOSURE_RESERVED,
    EXPOSURE_RELEASED,
    PRE_SUBMIT_EVALUATED,
    ORDER_PREPARED,
    ORDER_STATE_CHANGED,
    CANCELLATION_PREPARED,
    CANCELLATION_STATE_CHANGED,
    BROKER_ORDER_EVENT_RECORDED,
    BROKER_EXECUTION_EVENT_RECORDED,
    FILL_RECORDED,
    POSITION_SNAPSHOT_RECORDED,
    POST_FILL_EVALUATED,
    POST_FILL_SAFETY_RESPONSE_CREATED,
    EXPOSURE_ADJUSTED,
    DATA_SNAPSHOT_CREATED,
    REPLAY_COMPLETED,
    RESEARCH_COMPLETED,
    INTEGRITY_VERIFICATION_FAILED
}

public sealed record AuditEventRecord
{
    public AuditEventRecord(
        Guid eventId,
        long sequence,
        AuditEventType eventType,
        DateTimeOffset eventTime,
        OperatorId actorId,
        GoalVersionId? goalVersionId,
        SessionId? sessionId,
        IReadOnlyDictionary<string, string> relatedRecordIdentifiers,
        string? previousState,
        string newState,
        string reason,
        string sourceReference,
        string canonicalPayload,
        string previousHash,
        string eventHash)
    {
        if (eventId == Guid.Empty || sequence <= 0 || actorId.Value == Guid.Empty)
        {
            throw new DomainRuleViolationException("The Audit event identity is invalid.");
        }

        if (eventTime.Offset != TimeSpan.Zero)
        {
            throw new DomainRuleViolationException("The Audit event time must use UTC.");
        }

        EventId = eventId;
        Sequence = sequence;
        EventType = eventType;
        EventTime = eventTime;
        ActorId = actorId;
        GoalVersionId = goalVersionId;
        SessionId = sessionId;
        RelatedRecordIdentifiers = relatedRecordIdentifiers
            .ToImmutableSortedDictionary(StringComparer.Ordinal);
        PreviousState = previousState;
        NewState = RequireText(newState);
        Reason = RequireText(reason);
        SourceReference = RequireText(sourceReference);
        CanonicalPayload = RequireText(canonicalPayload);
        PreviousHash = RequireHash(previousHash);
        EventHash = RequireHash(eventHash);
    }

    public Guid EventId { get; }

    public long Sequence { get; }

    public AuditEventType EventType { get; }

    public DateTimeOffset EventTime { get; }

    public OperatorId ActorId { get; }

    public GoalVersionId? GoalVersionId { get; }

    public SessionId? SessionId { get; }

    public ImmutableSortedDictionary<string, string> RelatedRecordIdentifiers { get; }

    public string? PreviousState { get; }

    public string NewState { get; }

    public string Reason { get; }

    public string SourceReference { get; }

    public string CanonicalPayload { get; }

    public string PreviousHash { get; }

    public string EventHash { get; }

    private static string RequireText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainRuleViolationException("The Audit event contains missing text.");
        }

        return value;
    }

    private static string RequireHash(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new DomainRuleViolationException("The Audit event contains an invalid hash.");
        }

        return value.ToLowerInvariant();
    }
}
