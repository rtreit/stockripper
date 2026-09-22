using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Execution;

public enum OrderSide
{
    BUY,
    SELL
}

public enum IntentOrderType
{
    LIMIT
}

public enum IntentTimeInForce
{
    DAY
}

public enum ExecutionIntentState
{
    CREATED,
    RISK_ACCEPTED,
    RISK_REJECTED,
    REVIEW_PENDING,
    APPROVED,
    REJECTED,
    EXPIRED,
    SUBMISSION_PENDING,
    SUBMITTED,
    SUBMISSION_UNKNOWN,
    CANCELED,
    FAILED
}

public sealed record IntentAccountBinding
{
    public IntentAccountBinding(
        VersionReference brokerConnectionVersion,
        BrokerEnvironment environment,
        string endpointIdentity,
        string accountIdentifier)
    {
        ArgumentNullException.ThrowIfNull(brokerConnectionVersion);
        if (string.IsNullOrWhiteSpace(endpointIdentity) || endpointIdentity != endpointIdentity.Trim())
        {
            throw new DomainRuleViolationException("The intent endpoint identity is required.");
        }

        if (string.IsNullOrWhiteSpace(accountIdentifier) || accountIdentifier != accountIdentifier.Trim())
        {
            throw new DomainRuleViolationException("The intent account identifier is required.");
        }

        BrokerConnectionVersion = brokerConnectionVersion;
        Environment = environment;
        EndpointIdentity = endpointIdentity;
        AccountIdentifier = accountIdentifier;
    }

    public VersionReference BrokerConnectionVersion { get; }
    public BrokerEnvironment Environment { get; }
    public string EndpointIdentity { get; }
    public string AccountIdentifier { get; }
}

public sealed record ExecutionIntent
{
    private const string DigestSchema = "stockripper-execution-intent-v1";

    private ExecutionIntent(
        ExecutionIntentId id,
        DecisionId decisionId,
        GoalVersionId goalVersionId,
        SessionId sessionId,
        ExecutionMode mode,
        IntentAccountBinding accountBinding,
        string instrumentIdentifier,
        OrderSide side,
        IntentOrderType orderType,
        decimal quantity,
        decimal limitPrice,
        IntentTimeInForce timeInForce,
        DateTimeOffset expiresAt,
        RiskEvaluationId riskEvaluationId,
        string idempotencyKey,
        DateTimeOffset createdAt,
        ExecutionIntentState state,
        string contentDigest)
    {
        Id = id;
        DecisionId = decisionId;
        GoalVersionId = goalVersionId;
        SessionId = sessionId;
        Mode = mode;
        AccountBinding = accountBinding;
        InstrumentIdentifier = instrumentIdentifier;
        Side = side;
        OrderType = orderType;
        Quantity = quantity;
        LimitPrice = limitPrice;
        TimeInForce = timeInForce;
        ExpiresAt = expiresAt;
        RiskEvaluationId = riskEvaluationId;
        IdempotencyKey = idempotencyKey;
        CreatedAt = createdAt;
        State = state;
        ContentDigest = contentDigest;
    }

    public ExecutionIntentId Id { get; }
    public DecisionId DecisionId { get; }
    public GoalVersionId GoalVersionId { get; }
    public SessionId SessionId { get; }
    public ExecutionMode Mode { get; }
    public IntentAccountBinding AccountBinding { get; }
    public string InstrumentIdentifier { get; }
    public OrderSide Side { get; }
    public IntentOrderType OrderType { get; }
    public decimal Quantity { get; }
    public decimal LimitPrice { get; }
    public IntentTimeInForce TimeInForce { get; }
    public DateTimeOffset ExpiresAt { get; }
    public RiskEvaluationId RiskEvaluationId { get; }
    public string IdempotencyKey { get; }
    public DateTimeOffset CreatedAt { get; }
    public ExecutionIntentState State { get; private init; }
    public string ContentDigest { get; }
    public decimal MaximumExposure => Quantity * LimitPrice;

    public static ExecutionIntent Create(
        ExecutionIntentId id,
        DecisionId decisionId,
        GoalVersionId goalVersionId,
        SessionId sessionId,
        ExecutionMode mode,
        IntentAccountBinding accountBinding,
        string instrumentIdentifier,
        OrderSide side,
        IntentOrderType orderType,
        decimal quantity,
        decimal limitPrice,
        IntentTimeInForce timeInForce,
        DateTimeOffset expiresAt,
        RiskEvaluationId riskEvaluationId,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(accountBinding);
        if (mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || accountBinding.Environment != BrokerEnvironment.PAPER)
        {
            throw new DomainRuleViolationException(
                "This intent slice permits only paper execution authority.");
        }

        var instrument = RequireText(instrumentIdentifier, "The intent instrument identifier is required.");
        if (quantity <= 0m || limitPrice <= 0m)
        {
            throw new DomainRuleViolationException("The intent quantity and limit price must be greater than zero.");
        }

        var created = UtcTimestamp.Require(createdAt, nameof(createdAt));
        var expiry = UtcTimestamp.Require(expiresAt, nameof(expiresAt));
        if (expiry <= created)
        {
            throw new DomainRuleViolationException("The intent expiry must be after creation.");
        }

        var idempotencyKey = $"stockripper-intent:{id.Value:D}";
        var digest = ComputeDigest(
            id,
            decisionId,
            goalVersionId,
            sessionId,
            mode,
            accountBinding,
            instrument,
            side,
            orderType,
            quantity,
            limitPrice,
            timeInForce,
            expiry,
            riskEvaluationId,
            idempotencyKey,
            created);
        return new ExecutionIntent(
            id,
            decisionId,
            goalVersionId,
            sessionId,
            mode,
            accountBinding,
            instrument,
            side,
            orderType,
            quantity,
            limitPrice,
            timeInForce,
            expiry,
            riskEvaluationId,
            idempotencyKey,
            created,
            ExecutionIntentState.CREATED,
            digest);
    }

    public static ExecutionIntent Restore(
        ExecutionIntentId id,
        DecisionId decisionId,
        GoalVersionId goalVersionId,
        SessionId sessionId,
        ExecutionMode mode,
        IntentAccountBinding accountBinding,
        string instrumentIdentifier,
        OrderSide side,
        IntentOrderType orderType,
        decimal quantity,
        decimal limitPrice,
        IntentTimeInForce timeInForce,
        DateTimeOffset expiresAt,
        RiskEvaluationId riskEvaluationId,
        string idempotencyKey,
        DateTimeOffset createdAt,
        ExecutionIntentState state,
        string contentDigest)
    {
        var restored = Create(
            id,
            decisionId,
            goalVersionId,
            sessionId,
            mode,
            accountBinding,
            instrumentIdentifier,
            side,
            orderType,
            quantity,
            limitPrice,
            timeInForce,
            expiresAt,
            riskEvaluationId,
            createdAt);
        if (!string.Equals(restored.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
            || !string.Equals(restored.ContentDigest, contentDigest, StringComparison.Ordinal)
            || !Enum.IsDefined(state))
        {
            throw new InvalidDataException("The stored Execution intent integrity check failed.");
        }

        return restored with { State = state };
    }

    public ExecutionIntent ApplyRisk(RiskEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (State != ExecutionIntentState.CREATED
            || evaluation.Id != RiskEvaluationId
            || evaluation.IntentId != Id
            || evaluation.DecisionId != DecisionId
            || evaluation.GoalVersionId != GoalVersionId
            || evaluation.SessionId != SessionId
            || evaluation.RequestedExposure != MaximumExposure)
        {
            throw new DomainRuleViolationException("The risk evaluation does not match the Execution intent.");
        }

        return this with
        {
            State = evaluation.Result == RiskEvaluationResult.ACCEPTED
                ? ExecutionIntentState.RISK_ACCEPTED
                : ExecutionIntentState.RISK_REJECTED
        };
    }

    public ExecutionIntent AwaitReview()
    {
        if (Mode != ExecutionMode.PAPER_REVIEW || State != ExecutionIntentState.RISK_ACCEPTED)
        {
            throw new DomainRuleViolationException("The Execution intent cannot enter review.");
        }

        return this with { State = ExecutionIntentState.REVIEW_PENDING };
    }

    public ExecutionIntent ApplyReview(ReviewApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (State != ExecutionIntentState.REVIEW_PENDING
            || approval.IntentId != Id
            || !string.Equals(approval.IntentDigest, ContentDigest, StringComparison.Ordinal)
            || approval.ActionTime >= ExpiresAt
            || approval.ExpiresAt > ExpiresAt)
        {
            throw new DomainRuleViolationException("The review does not authorize this Execution intent.");
        }

        return this with
        {
            State = approval.Result == ReviewResult.APPROVED
                ? ExecutionIntentState.APPROVED
                : ExecutionIntentState.REJECTED
        };
    }

    public ExecutionIntent Expire(DateTimeOffset at)
    {
        var time = UtcTimestamp.Require(at, nameof(at));
        if (time < ExpiresAt
            || State is not (ExecutionIntentState.REVIEW_PENDING or ExecutionIntentState.APPROVED))
        {
            throw new DomainRuleViolationException("The Execution intent cannot expire.");
        }

        return this with { State = ExecutionIntentState.EXPIRED };
    }

    public ExecutionIntent ExpireApproval(DateTimeOffset at, ReviewApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var time = UtcTimestamp.Require(at, nameof(at));
        if (State != ExecutionIntentState.APPROVED
            || approval.Result != ReviewResult.APPROVED
            || approval.IntentId != Id
            || !string.Equals(approval.IntentDigest, ContentDigest, StringComparison.Ordinal)
            || !approval.HasValidContentDigest()
            || approval.ExpiresAt > ExpiresAt
            || time < approval.ExpiresAt)
        {
            throw new DomainRuleViolationException("The Review approval cannot expire this Execution intent.");
        }

        return this with { State = ExecutionIntentState.EXPIRED };
    }

    public ExecutionIntent BeginSubmission(PreSubmitEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var authorityStateValid = Mode == ExecutionMode.PAPER_REVIEW
            ? State == ExecutionIntentState.APPROVED
            : Mode == ExecutionMode.PAPER_AUTO
                && State == ExecutionIntentState.RISK_ACCEPTED;
        if (!authorityStateValid
            || evaluation.IntentId != Id
            || evaluation.Result != PreSubmitEvaluationResult.PASSED
            || !string.Equals(evaluation.IntentDigest, ContentDigest, StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException("The Execution intent cannot begin submission.");
        }

        return this with { State = ExecutionIntentState.SUBMISSION_PENDING };
    }

    public ExecutionIntent MarkSubmitted(OrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (State is not (ExecutionIntentState.SUBMISSION_PENDING or ExecutionIntentState.SUBMISSION_UNKNOWN)
            || order.IntentId != Id
            || order.State != OrderState.ACKNOWLEDGED)
        {
            throw new DomainRuleViolationException("The Execution intent cannot be marked submitted.");
        }

        return this with { State = ExecutionIntentState.SUBMITTED };
    }

    public ExecutionIntent MarkSubmissionUnknown(OrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (State != ExecutionIntentState.SUBMISSION_PENDING
            || order.IntentId != Id
            || order.State != OrderState.SUBMISSION_UNKNOWN)
        {
            throw new DomainRuleViolationException("The Execution intent cannot enter unknown submission state.");
        }

        return this with { State = ExecutionIntentState.SUBMISSION_UNKNOWN };
    }

    public ExecutionIntent MarkSubmissionFailed(OrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (State is not (ExecutionIntentState.SUBMISSION_PENDING or ExecutionIntentState.SUBMISSION_UNKNOWN)
            || order.IntentId != Id
            || order.State is not (OrderState.REJECTED or OrderState.RECONCILIATION_REQUIRED))
        {
            throw new DomainRuleViolationException("The Execution intent cannot be marked failed.");
        }

        return this with { State = ExecutionIntentState.FAILED };
    }

    public ExecutionIntent MarkCanceled(OrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (State is not (ExecutionIntentState.SUBMITTED or ExecutionIntentState.SUBMISSION_UNKNOWN)
            || order.IntentId != Id
            || order.State != OrderState.CANCELED)
        {
            throw new DomainRuleViolationException("The Execution intent cannot be marked canceled.");
        }

        return this with { State = ExecutionIntentState.CANCELED };
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(
            Id,
            DecisionId,
            GoalVersionId,
            SessionId,
            Mode,
            AccountBinding,
            InstrumentIdentifier,
            Side,
            OrderType,
            Quantity,
            LimitPrice,
            TimeInForce,
            ExpiresAt,
            RiskEvaluationId,
            IdempotencyKey,
            CreatedAt),
        StringComparison.Ordinal);

    private static string ComputeDigest(
        ExecutionIntentId id,
        DecisionId decisionId,
        GoalVersionId goalVersionId,
        SessionId sessionId,
        ExecutionMode mode,
        IntentAccountBinding accountBinding,
        string instrumentIdentifier,
        OrderSide side,
        IntentOrderType orderType,
        decimal quantity,
        decimal limitPrice,
        IntentTimeInForce timeInForce,
        DateTimeOffset expiresAt,
        RiskEvaluationId riskEvaluationId,
        string idempotencyKey,
        DateTimeOffset createdAt)
    {
        var value = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            DecisionId = decisionId.Value,
            GoalVersionId = goalVersionId.Value,
            SessionId = sessionId.Value,
            Mode = mode.ToWireValue(),
            AccountBinding = new
            {
                BrokerConnectionVersion = new
                {
                    accountBinding.BrokerConnectionVersion.Identifier,
                    accountBinding.BrokerConnectionVersion.Version,
                    accountBinding.BrokerConnectionVersion.Digest
                },
                Environment = accountBinding.Environment.ToString(),
                accountBinding.EndpointIdentity,
                accountBinding.AccountIdentifier
            },
            InstrumentIdentifier = instrumentIdentifier,
            Side = side.ToString(),
            OrderType = orderType.ToString(),
            Quantity = CanonicalDecimal(quantity),
            LimitPrice = CanonicalDecimal(limitPrice),
            TimeInForce = timeInForce.ToString(),
            ExpiresAt = expiresAt.ToString("O"),
            RiskEvaluationId = riskEvaluationId.Value,
            IdempotencyKey = idempotencyKey,
            CreatedAt = createdAt.ToString("O")
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }

    private static string CanonicalDecimal(decimal value) => value.ToString(
        "G29",
        CultureInfo.InvariantCulture);
}
