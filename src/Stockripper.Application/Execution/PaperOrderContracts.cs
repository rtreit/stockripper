using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Application.Execution;

public enum PaperBrokerResultType
{
    ACKNOWLEDGED,
    REJECTED,
    NOT_FOUND,
    UNAVAILABLE
}

public sealed record PaperBrokerResult(
    PaperBrokerResultType Type,
    string? BrokerOrderId,
    string? EventReference,
    DateTimeOffset ObservedAt)
{
    public static PaperBrokerResult Acknowledged(
        string brokerOrderId,
        string eventReference,
        DateTimeOffset observedAt) =>
        new(PaperBrokerResultType.ACKNOWLEDGED, brokerOrderId, eventReference, observedAt);

    public static PaperBrokerResult Rejected(string eventReference, DateTimeOffset observedAt) =>
        new(PaperBrokerResultType.REJECTED, null, eventReference, observedAt);

    public static PaperBrokerResult NotFound(DateTimeOffset observedAt) =>
        new(PaperBrokerResultType.NOT_FOUND, null, null, observedAt);

    public static PaperBrokerResult Unavailable(DateTimeOffset observedAt) =>
        new(PaperBrokerResultType.UNAVAILABLE, null, null, observedAt);
}

public interface IPaperBrokerOrderAdapter
{
    ValueTask<PaperBrokerResult> SendAsync(
        OrderRecord preparedOrder,
        CancellationToken cancellationToken);

    ValueTask<PaperBrokerResult> FindByClientOrderIdAsync(
        IntentAccountBinding accountBinding,
        string brokerClientOrderId,
        CancellationToken cancellationToken);

    ValueTask<PaperBrokerCancellationResult> RequestCancellationAsync(
        CancellationRequest cancellationRequest,
        CancellationToken cancellationToken);
}

public sealed class PaperBrokerOutcomeUnknownException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public enum PaperBrokerCancellationResultType
{
    ACCEPTED,
    UNAVAILABLE
}

public sealed record PaperBrokerCancellationResult(
    PaperBrokerCancellationResultType Type,
    string? BrokerRequestReference,
    DateTimeOffset ObservedAt)
{
    public static PaperBrokerCancellationResult Accepted(
        string brokerRequestReference, DateTimeOffset observedAt) =>
        new(PaperBrokerCancellationResultType.ACCEPTED, brokerRequestReference, observedAt);

    public static PaperBrokerCancellationResult Unavailable(DateTimeOffset observedAt) =>
        new(PaperBrokerCancellationResultType.UNAVAILABLE, null, observedAt);
}

public sealed class PaperBrokerCancellationOutcomeUnknownException(
    string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

public interface IPaperOrderPersistence
{
    ValueTask RecordEvaluationAsync(
        PreSubmitEvaluation evaluation,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask PrepareAsync(
        PreSubmitEvaluation evaluation,
        OrderRecord order,
        ExecutionIntent pendingIntent,
        ExecutionIntentState expectedIntentState,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<OrderRecord?> FindOrderAsync(
        OrderId orderId,
        CancellationToken cancellationToken);

    ValueTask<OrderRecord?> FindByIntentAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken);

    ValueTask<PreSubmitEvaluation?> FindEvaluationAsync(
        PreSubmitEvaluationId evaluationId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<PreSubmitEvaluation?>(null);

    ValueTask RecordTransitionAsync(
        OrderRecord order,
        OrderState expectedOrderState,
        ExecutionIntent intent,
        ExecutionIntentState expectedIntentState,
        BrokerOrderEvent? brokerEvent,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<OrderRecord>> FindRecoverableAsync(
        CancellationToken cancellationToken);
}

public sealed class PaperOrderException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed record PaperOrderPreparation(PreSubmitEvaluation Evaluation, OrderRecord? Order);
