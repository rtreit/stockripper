using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;

namespace Stockripper.Domain.Execution;

public enum PreSubmitCheckCode
{
    ACTIVE_AUTHORITY,
    ACCOUNT_BINDING,
    APPROVAL_CURRENT,
    RECONCILIATION_CURRENT,
    EXPOSURE_RESERVED,
    CAPITAL_ALLOCATION,
    BUYING_POWER,
    INSTRUMENT_PERMISSION,
    PRICE_VALID,
    DATA_FRESHNESS,
    SCHEDULE_OPEN,
    KILL_SWITCH_CLEAR,
    POST_FILL_CURRENT
}

public sealed record PreSubmitCheck(PreSubmitCheckCode Code, bool Passed, string Reason)
{
    public string Reason { get; } = RequireReason(Reason);

    private static string RequireReason(string value) =>
        string.IsNullOrWhiteSpace(value) || value != value.Trim()
            ? throw new DomainRuleViolationException("The pre-submit check reason is required.")
            : value;
}

public enum PreSubmitEvaluationResult
{
    PASSED,
    FAILED
}

public sealed record PreSubmitEvaluation
{
    private const string DigestSchema = "stockripper-pre-submit-evaluation-v1";

    private PreSubmitEvaluation(
        PreSubmitEvaluationId id,
        ExecutionIntentId intentId,
        string intentDigest,
        ReviewApprovalId? approvalId,
        ExposureReservationId reservationId,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        IReadOnlyList<PreSubmitCheck> checks,
        PreSubmitEvaluationResult result,
        DateTimeOffset evaluatedAt,
        string contentDigest)
    {
        Id = id;
        IntentId = intentId;
        IntentDigest = intentDigest;
        ApprovalId = approvalId;
        ReservationId = reservationId;
        ReconciliationId = reconciliationId;
        PositionExposure = positionExposure;
        OpenOrderExposure = openOrderExposure;
        BuyingPower = buyingPower;
        Currency = currency;
        Checks = checks.ToArray();
        Result = result;
        EvaluatedAt = evaluatedAt;
        ContentDigest = contentDigest;
    }

    public PreSubmitEvaluationId Id { get; }
    public ExecutionIntentId IntentId { get; }
    public string IntentDigest { get; }
    public ReviewApprovalId? ApprovalId { get; }
    public ExposureReservationId ReservationId { get; }
    public ReconciliationId ReconciliationId { get; }
    public decimal PositionExposure { get; }
    public decimal OpenOrderExposure { get; }
    public decimal BuyingPower { get; }
    public string Currency { get; }
    public IReadOnlyList<PreSubmitCheck> Checks { get; }
    public PreSubmitEvaluationResult Result { get; }
    public DateTimeOffset EvaluatedAt { get; }
    public string ContentDigest { get; }

    public static PreSubmitEvaluation Create(
        PreSubmitEvaluationId id,
        ExecutionIntent intent,
        ReviewApproval approval,
        ExposureReservation reservation,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        IReadOnlyList<PreSubmitCheck> checks,
        DateTimeOffset evaluatedAt) => CreateCore(
        id, intent, approval, reservation, reconciliationId, positionExposure,
        openOrderExposure, buyingPower, currency, checks, evaluatedAt);

    public static PreSubmitEvaluation CreateAutomatic(
        PreSubmitEvaluationId id,
        ExecutionIntent intent,
        ExposureReservation reservation,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        IReadOnlyList<PreSubmitCheck> checks,
        DateTimeOffset evaluatedAt) => CreateCore(
        id, intent, null, reservation, reconciliationId, positionExposure,
        openOrderExposure, buyingPower, currency, checks, evaluatedAt);

    private static PreSubmitEvaluation CreateCore(
        PreSubmitEvaluationId id,
        ExecutionIntent intent,
        ReviewApproval? approval,
        ExposureReservation reservation,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        IReadOnlyList<PreSubmitCheck> checks,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(checks);
        var time = UtcTimestamp.Require(evaluatedAt, nameof(evaluatedAt));
        var authorityMatches = intent.Mode switch
        {
            ExecutionMode.PAPER_REVIEW => intent.State == ExecutionIntentState.APPROVED
                && approval?.Result == ReviewResult.APPROVED
                && approval.IntentId == intent.Id
                && string.Equals(approval.IntentDigest, intent.ContentDigest,
                    StringComparison.Ordinal),
            ExecutionMode.PAPER_AUTO => intent.State == ExecutionIntentState.RISK_ACCEPTED
                && approval is null,
            _ => false
        };
        if (!authorityMatches
            || intent.AccountBinding.Environment != BrokerEnvironment.PAPER
            || reservation.State != ExposureReservationState.RESERVED
            || reservation.IntentId != intent.Id
            || reservation.AccountBinding != intent.AccountBinding
            || reservation.Amount != intent.MaximumExposure)
        {
            throw new DomainRuleViolationException("The pre-submit authority does not match the Execution intent.");
        }

        if (positionExposure < 0m || openOrderExposure < 0m || buyingPower < 0m
            || string.IsNullOrWhiteSpace(currency) || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new DomainRuleViolationException("The pre-submit capital observation is invalid.");
        }

        var orderedChecks = checks.OrderBy(check => check.Code).ToArray();
        if (orderedChecks.Length != Enum.GetValues<PreSubmitCheckCode>().Length
            || orderedChecks.Select(check => check.Code).Distinct().Count() != orderedChecks.Length)
        {
            throw new DomainRuleViolationException("The pre-submit evaluation requires each check exactly once.");
        }

        var result = orderedChecks.All(check => check.Passed)
            ? PreSubmitEvaluationResult.PASSED
            : PreSubmitEvaluationResult.FAILED;
        var digest = ComputeDigest(id, intent.Id, intent.ContentDigest, approval?.Id,
            reservation.Id,
            reconciliationId, positionExposure, openOrderExposure, buyingPower, currency,
            orderedChecks, result, time);
        return new PreSubmitEvaluation(id, intent.Id, intent.ContentDigest, approval?.Id,
            reservation.Id,
            reconciliationId, positionExposure, openOrderExposure, buyingPower, currency,
            orderedChecks, result, time, digest);
    }

    public static PreSubmitEvaluation Restore(
        PreSubmitEvaluationId id,
        ExecutionIntentId intentId,
        string intentDigest,
        ReviewApprovalId? approvalId,
        ExposureReservationId reservationId,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        IReadOnlyList<PreSubmitCheck> checks,
        PreSubmitEvaluationResult result,
        DateTimeOffset evaluatedAt,
        string contentDigest)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var time = UtcTimestamp.Require(evaluatedAt, nameof(evaluatedAt));
        var orderedChecks = checks.OrderBy(check => check.Code).ToArray();
        if (string.IsNullOrWhiteSpace(intentDigest)
            || intentDigest.Length != 64
            || intentDigest.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || positionExposure < 0m || openOrderExposure < 0m || buyingPower < 0m
            || string.IsNullOrWhiteSpace(currency) || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z')
            || orderedChecks.Length != Enum.GetValues<PreSubmitCheckCode>().Length
            || orderedChecks.Select(check => check.Code).Distinct().Count()
                != orderedChecks.Length
            || result != (orderedChecks.All(check => check.Passed)
                ? PreSubmitEvaluationResult.PASSED
                : PreSubmitEvaluationResult.FAILED))
        {
            throw new InvalidDataException("The stored pre-submit evaluation is invalid.");
        }

        var expectedDigest = ComputeDigest(id, intentId, intentDigest, approvalId,
            reservationId, reconciliationId, positionExposure, openOrderExposure,
            buyingPower, currency, orderedChecks, result, time);
        if (!string.Equals(expectedDigest, contentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored pre-submit evaluation integrity check failed.");
        }

        return new PreSubmitEvaluation(id, intentId, intentDigest, approvalId,
            reservationId, reconciliationId, positionExposure, openOrderExposure,
            buyingPower, currency, orderedChecks, result, time, contentDigest);
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(Id, IntentId, IntentDigest, ApprovalId, ReservationId,
            ReconciliationId, PositionExposure, OpenOrderExposure, BuyingPower,
            Currency, Checks, Result, EvaluatedAt),
        StringComparison.Ordinal);

    private static string ComputeDigest(
        PreSubmitEvaluationId id,
        ExecutionIntentId intentId,
        string intentDigest,
        ReviewApprovalId? approvalId,
        ExposureReservationId reservationId,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        IReadOnlyList<PreSubmitCheck> checks,
        PreSubmitEvaluationResult result,
        DateTimeOffset evaluatedAt)
    {
        var value = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            IntentId = intentId.Value,
            IntentDigest = intentDigest,
            ApprovalId = approvalId?.Value,
            ReservationId = reservationId.Value,
            ReconciliationId = reconciliationId.Value,
            PositionExposure = Canonical(positionExposure),
            OpenOrderExposure = Canonical(openOrderExposure),
            BuyingPower = Canonical(buyingPower),
            Currency = currency,
            Checks = checks.Select(check => new { Code = check.Code.ToString(), check.Passed, check.Reason }),
            Result = result.ToString(),
            EvaluatedAt = evaluatedAt.ToString("O")
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static string Canonical(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}

public sealed record OrderRequestSnapshot(
    string InstrumentIdentifier,
    OrderSide Side,
    IntentOrderType OrderType,
    decimal Quantity,
    decimal LimitPrice,
    IntentTimeInForce TimeInForce)
{
    public static OrderRequestSnapshot FromIntent(ExecutionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (decimal.Truncate(intent.Quantity) != intent.Quantity)
        {
            throw new DomainRuleViolationException("The initial Broker scope permits only whole shares.");
        }

        return new(intent.InstrumentIdentifier, intent.Side, intent.OrderType,
            intent.Quantity, intent.LimitPrice, intent.TimeInForce);
    }
}

public enum OrderState
{
    CREATED,
    SUBMITTING,
    ACKNOWLEDGED,
    PARTIALLY_FILLED,
    FILLED,
    CANCEL_PENDING,
    CANCELED,
    REJECTED,
    EXPIRED,
    SUBMISSION_UNKNOWN,
    RECONCILIATION_REQUIRED
}

public enum BrokerOrderEventResult
{
    ACKNOWLEDGED,
    REJECTED
}

public sealed record BrokerOrderEvent
{
    private BrokerOrderEvent(BrokerOrderEventId id, OrderId orderId,
        string brokerClientOrderId, string? brokerOrderId, string eventReference,
        BrokerOrderEventResult result, DateTimeOffset observedAt, string contentDigest)
    {
        Id = id;
        OrderId = orderId;
        BrokerClientOrderId = brokerClientOrderId;
        BrokerOrderId = brokerOrderId;
        EventReference = eventReference;
        Result = result;
        ObservedAt = observedAt;
        ContentDigest = contentDigest;
    }

    public BrokerOrderEventId Id { get; }
    public OrderId OrderId { get; }
    public string BrokerClientOrderId { get; }
    public string? BrokerOrderId { get; }
    public string EventReference { get; }
    public BrokerOrderEventResult Result { get; }
    public DateTimeOffset ObservedAt { get; }
    public string ContentDigest { get; }

    public static BrokerOrderEvent Create(BrokerOrderEventId id, OrderRecord order,
        string? brokerOrderId, string eventReference, BrokerOrderEventResult result,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        var time = UtcTimestamp.Require(observedAt, nameof(observedAt));
        if (order.State is not (OrderState.SUBMITTING or OrderState.SUBMISSION_UNKNOWN)
            || string.IsNullOrWhiteSpace(eventReference)
            || (result == BrokerOrderEventResult.ACKNOWLEDGED) != !string.IsNullOrWhiteSpace(brokerOrderId))
        {
            throw new DomainRuleViolationException("The Broker Order event is invalid.");
        }

        var value = new
        {
            Schema = "stockripper-broker-order-event-v1",
            Id = id.Value,
            OrderId = order.Id.Value,
            order.BrokerClientOrderId,
            BrokerOrderId = brokerOrderId,
            EventReference = eventReference,
            Result = result.ToString(),
            ObservedAt = time.ToString("O")
        };
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        return new(id, order.Id, order.BrokerClientOrderId, brokerOrderId,
            eventReference, result, time, digest);
    }
}

public sealed record OrderRecord
{
    private OrderRecord(
        OrderId id,
        ExecutionIntentId intentId,
        string intentDigest,
        PreSubmitEvaluationId preSubmitEvaluationId,
        IntentAccountBinding accountBinding,
        OrderRequestSnapshot request,
        string idempotencyKey,
        string brokerClientOrderId,
        OrderState state,
        string? brokerOrderId,
        string? brokerEventReference,
        decimal filledQuantity,
        DateTimeOffset firstSubmissionTime,
        DateTimeOffset lastUpdatedAt,
        DateTimeOffset? terminalAt)
    {
        Id = id;
        IntentId = intentId;
        IntentDigest = intentDigest;
        PreSubmitEvaluationId = preSubmitEvaluationId;
        AccountBinding = accountBinding;
        Request = request;
        IdempotencyKey = idempotencyKey;
        BrokerClientOrderId = brokerClientOrderId;
        State = state;
        BrokerOrderId = brokerOrderId;
        BrokerEventReference = brokerEventReference;
        FilledQuantity = filledQuantity;
        FirstSubmissionTime = firstSubmissionTime;
        LastUpdatedAt = lastUpdatedAt;
        TerminalAt = terminalAt;
    }

    public OrderId Id { get; }
    public ExecutionIntentId IntentId { get; }
    public string IntentDigest { get; }
    public PreSubmitEvaluationId PreSubmitEvaluationId { get; }
    public IntentAccountBinding AccountBinding { get; }
    public OrderRequestSnapshot Request { get; }
    public string IdempotencyKey { get; }
    public string BrokerClientOrderId { get; }
    public OrderState State { get; private init; }
    public string? BrokerOrderId { get; private init; }
    public string? BrokerEventReference { get; private init; }
    public decimal FilledQuantity { get; private init; }
    public DateTimeOffset FirstSubmissionTime { get; }
    public DateTimeOffset LastUpdatedAt { get; private init; }
    public DateTimeOffset? TerminalAt { get; private init; }

    public static OrderRecord Prepare(OrderId id, ExecutionIntent intent,
        PreSubmitEvaluation evaluation, DateTimeOffset preparedAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(evaluation);
        var time = UtcTimestamp.Require(preparedAt, nameof(preparedAt));
        var authorityStateValid = intent.Mode == ExecutionMode.PAPER_REVIEW
            ? intent.State == ExecutionIntentState.APPROVED
            : intent.Mode == ExecutionMode.PAPER_AUTO
                && intent.State == ExecutionIntentState.RISK_ACCEPTED;
        if (!authorityStateValid
            || evaluation.Result != PreSubmitEvaluationResult.PASSED
            || evaluation.IntentId != intent.Id
            || !string.Equals(evaluation.IntentDigest, intent.ContentDigest, StringComparison.Ordinal)
            || time < evaluation.EvaluatedAt || time >= intent.ExpiresAt)
        {
            throw new DomainRuleViolationException("The Order cannot be prepared from this authority.");
        }

        return new OrderRecord(id, intent.Id, intent.ContentDigest, evaluation.Id,
            intent.AccountBinding, OrderRequestSnapshot.FromIntent(intent),
            $"stockripper-order:{intent.Id.Value:D}", $"sr-{intent.Id.Value:N}",
            OrderState.SUBMITTING, null, null, 0m, time, time, null);
    }

    public OrderRecord Acknowledge(string brokerOrderId, string eventReference, DateTimeOffset at) =>
        TransitionFromUncertain(OrderState.ACKNOWLEDGED, brokerOrderId, eventReference, at, terminal: false);

    public OrderRecord Reject(string eventReference, DateTimeOffset at) =>
        Transition(OrderState.SUBMITTING, OrderState.REJECTED, null, eventReference, at, terminal: true);

    public OrderRecord MarkSubmissionUnknown(DateTimeOffset at) =>
        Transition(OrderState.SUBMITTING, OrderState.SUBMISSION_UNKNOWN, null, null, at, terminal: false);

    public OrderRecord RequireReconciliation(DateTimeOffset at) =>
        Transition(OrderState.SUBMISSION_UNKNOWN, OrderState.RECONCILIATION_REQUIRED, null, null, at, terminal: true);

    public OrderRecord RequestCancellation(DateTimeOffset at)
    {
        if (State is not (OrderState.ACKNOWLEDGED or OrderState.PARTIALLY_FILLED))
        {
            throw new DomainRuleViolationException("The Order cannot request cancellation.");
        }

        return Transition(State, OrderState.CANCEL_PENDING, BrokerOrderId,
            BrokerEventReference, at, terminal: false);
    }

    public OrderRecord Apply(BrokerExecutionEvent brokerEvent)
    {
        ArgumentNullException.ThrowIfNull(brokerEvent);
        if (brokerEvent.OrderId != Id
            || brokerEvent.AccountBinding != AccountBinding
            || !string.Equals(brokerEvent.BrokerClientOrderId, BrokerClientOrderId, StringComparison.Ordinal)
            || (BrokerOrderId is not null
                && !string.Equals(BrokerOrderId, brokerEvent.BrokerOrderId, StringComparison.Ordinal))
            || brokerEvent.ReceivedAt < LastUpdatedAt)
        {
            throw new DomainRuleViolationException("The Broker event does not match the Order authority.");
        }

        if (brokerEvent.CumulativeFilledQuantity < FilledQuantity)
        {
            return this;
        }

        if (IsTerminal(State))
        {
            var matchingTerminal = brokerEvent.Kind switch
            {
                BrokerExecutionEventKind.FILLED => State == OrderState.FILLED,
                BrokerExecutionEventKind.CANCELED => State == OrderState.CANCELED,
                BrokerExecutionEventKind.REJECTED => State == OrderState.REJECTED,
                BrokerExecutionEventKind.EXPIRED => State == OrderState.EXPIRED,
                _ => true
            };
            if (!matchingTerminal)
            {
                throw new DomainRuleViolationException("The Broker event conflicts with the terminal Order state.");
            }

            return this;
        }

        var target = brokerEvent.Kind switch
        {
            BrokerExecutionEventKind.ACKNOWLEDGED => OrderState.ACKNOWLEDGED,
            BrokerExecutionEventKind.PARTIALLY_FILLED => OrderState.PARTIALLY_FILLED,
            BrokerExecutionEventKind.FILLED => OrderState.FILLED,
            BrokerExecutionEventKind.CANCELED => OrderState.CANCELED,
            BrokerExecutionEventKind.REJECTED => OrderState.REJECTED,
            BrokerExecutionEventKind.EXPIRED => OrderState.EXPIRED,
            _ => throw new DomainRuleViolationException("The Broker event kind is invalid.")
        };

        if (brokerEvent.Kind == BrokerExecutionEventKind.ACKNOWLEDGED
            && State is OrderState.ACKNOWLEDGED or OrderState.PARTIALLY_FILLED or OrderState.CANCEL_PENDING)
        {
            return this;
        }

        if (brokerEvent.Kind == BrokerExecutionEventKind.PARTIALLY_FILLED
            && brokerEvent.CumulativeFilledQuantity == FilledQuantity)
        {
            return this;
        }

        return this with
        {
            State = target,
            BrokerOrderId = brokerEvent.BrokerOrderId,
            BrokerEventReference = brokerEvent.EventReference,
            FilledQuantity = brokerEvent.CumulativeFilledQuantity,
            LastUpdatedAt = brokerEvent.ReceivedAt,
            TerminalAt = IsTerminal(target) ? brokerEvent.ReceivedAt : null
        };
    }

    public static OrderRecord Restore(OrderId id, ExecutionIntentId intentId, string intentDigest,
        PreSubmitEvaluationId evaluationId, IntentAccountBinding accountBinding,
        OrderRequestSnapshot request, string idempotencyKey, string brokerClientOrderId,
        OrderState state, string? brokerOrderId, string? brokerEventReference,
        decimal filledQuantity,
        DateTimeOffset firstSubmissionTime, DateTimeOffset lastUpdatedAt, DateTimeOffset? terminalAt)
    {
        var first = UtcTimestamp.Require(firstSubmissionTime, nameof(firstSubmissionTime));
        var updated = UtcTimestamp.Require(lastUpdatedAt, nameof(lastUpdatedAt));
        if (updated < first || string.IsNullOrWhiteSpace(intentDigest)
            || string.IsNullOrWhiteSpace(idempotencyKey) || string.IsNullOrWhiteSpace(brokerClientOrderId)
            || filledQuantity < 0m || filledQuantity > request.Quantity
            || (state is OrderState.ACKNOWLEDGED or OrderState.PARTIALLY_FILLED
                or OrderState.CANCEL_PENDING or OrderState.FILLED
                && string.IsNullOrWhiteSpace(brokerOrderId))
            || (state == OrderState.PARTIALLY_FILLED
                && (filledQuantity <= 0m || filledQuantity >= request.Quantity))
            || (state == OrderState.FILLED && filledQuantity != request.Quantity)
            || (state is OrderState.FILLED or OrderState.CANCELED or OrderState.REJECTED
                or OrderState.EXPIRED or OrderState.RECONCILIATION_REQUIRED) != terminalAt.HasValue)
        {
            throw new InvalidDataException("The stored Order integrity check failed.");
        }

        return new(id, intentId, intentDigest, evaluationId, accountBinding, request,
            idempotencyKey, brokerClientOrderId, state, brokerOrderId, brokerEventReference,
            filledQuantity, first, updated, terminalAt);
    }

    private OrderRecord TransitionFromUncertain(OrderState target, string brokerOrderId,
        string eventReference, DateTimeOffset at, bool terminal)
    {
        if (State is not (OrderState.SUBMITTING or OrderState.SUBMISSION_UNKNOWN))
        {
            throw new DomainRuleViolationException("The Order cannot accept this Broker result.");
        }

        if (string.IsNullOrWhiteSpace(brokerOrderId) || string.IsNullOrWhiteSpace(eventReference))
        {
            throw new DomainRuleViolationException("The Broker acknowledgment identity is required.");
        }

        return Transition(State, target, brokerOrderId, eventReference, at, terminal);
    }

    private OrderRecord Transition(OrderState expected, OrderState target, string? brokerOrderId,
        string? eventReference, DateTimeOffset at, bool terminal)
    {
        var time = UtcTimestamp.Require(at, nameof(at));
        if (State != expected || time < LastUpdatedAt)
        {
            throw new DomainRuleViolationException("The Order transition is invalid.");
        }

        return this with
        {
            State = target,
            BrokerOrderId = brokerOrderId,
            BrokerEventReference = eventReference,
            LastUpdatedAt = time,
            TerminalAt = terminal ? time : null
        };
    }

    private static bool IsTerminal(OrderState state) => state is OrderState.FILLED
        or OrderState.CANCELED or OrderState.REJECTED or OrderState.EXPIRED
        or OrderState.RECONCILIATION_REQUIRED;
}
