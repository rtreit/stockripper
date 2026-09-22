namespace Stockripper.Domain.Common;

public readonly record struct GoalId
{
    public GoalId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static GoalId New() => new(Guid.NewGuid());
}

public readonly record struct GoalVersionId
{
    public GoalVersionId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static GoalVersionId New() => new(Guid.NewGuid());
}

public readonly record struct GoalDraftId
{
    public GoalDraftId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static GoalDraftId New() => new(Guid.NewGuid());
}

public readonly record struct ActivationId
{
    public ActivationId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ActivationId New() => new(Guid.NewGuid());
}

public readonly record struct SessionId
{
    public SessionId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static SessionId New() => new(Guid.NewGuid());
}

public readonly record struct DataSnapshotId
{
    public DataSnapshotId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static DataSnapshotId New() => new(Guid.NewGuid());
}

public readonly record struct ReplayResultId
{
    public ReplayResultId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ReplayResultId New() => new(Guid.NewGuid());
}

public readonly record struct DecisionId
{
    public DecisionId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static DecisionId New() => new(Guid.NewGuid());
}

public readonly record struct ResearchResultId
{
    public ResearchResultId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ResearchResultId New() => new(Guid.NewGuid());
}

public readonly record struct AccountAllocationEntryId
{
    public AccountAllocationEntryId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static AccountAllocationEntryId New() => new(Guid.NewGuid());
}

public readonly record struct SessionPreflightId
{
    public SessionPreflightId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static SessionPreflightId New() => new(Guid.NewGuid());
}

public readonly record struct RiskEvaluationId
{
    public RiskEvaluationId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static RiskEvaluationId New() => new(Guid.NewGuid());
}

public readonly record struct ExecutionIntentId
{
    public ExecutionIntentId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ExecutionIntentId New() => new(Guid.NewGuid());
}

public readonly record struct ReviewApprovalId
{
    public ReviewApprovalId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ReviewApprovalId New() => new(Guid.NewGuid());
}

public readonly record struct KillSwitchId
{
    public KillSwitchId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static KillSwitchId New() => new(Guid.NewGuid());
}

public readonly record struct ReconciliationId
{
    public ReconciliationId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ReconciliationId New() => new(Guid.NewGuid());
}

public readonly record struct ExposureReservationId
{
    public ExposureReservationId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ExposureReservationId New() => new(Guid.NewGuid());
}

public readonly record struct PreSubmitEvaluationId
{
    public PreSubmitEvaluationId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static PreSubmitEvaluationId New() => new(Guid.NewGuid());
}

public readonly record struct OrderId
{
    public OrderId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static OrderId New() => new(Guid.NewGuid());
}

public readonly record struct BrokerOrderEventId
{
    public BrokerOrderEventId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static BrokerOrderEventId New() => new(Guid.NewGuid());
}

public readonly record struct BrokerExecutionEventId
{
    public BrokerExecutionEventId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static BrokerExecutionEventId New() => new(Guid.NewGuid());
}

public readonly record struct FillId
{
    public FillId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static FillId New() => new(Guid.NewGuid());
}

public readonly record struct PositionSnapshotBatchId
{
    public PositionSnapshotBatchId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static PositionSnapshotBatchId New() => new(Guid.NewGuid());
}

public readonly record struct PositionSnapshotId
{
    public PositionSnapshotId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static PositionSnapshotId New() => new(Guid.NewGuid());
}

public readonly record struct PostFillEvaluationId
{
    public PostFillEvaluationId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static PostFillEvaluationId New() => new(Guid.NewGuid());
}

public readonly record struct PostFillSafetyResponseId
{
    public PostFillSafetyResponseId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static PostFillSafetyResponseId New() => new(Guid.NewGuid());
}

public readonly record struct CancellationRequestId
{
    public CancellationRequestId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static CancellationRequestId New() => new(Guid.NewGuid());
}

public readonly record struct OperatorId
{
    public OperatorId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static OperatorId New() => new(Guid.NewGuid());
}
