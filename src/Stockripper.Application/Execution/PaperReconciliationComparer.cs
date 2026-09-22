using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Application.Goals;

namespace Stockripper.Application.Execution;

public sealed record PaperReconciliationOrder(
    ExecutionIntentId? IntentId,
    string BrokerClientOrderId,
    string? BrokerOrderId,
    string InstrumentIdentifier,
    OrderSide Side,
    decimal Quantity,
    decimal LimitPrice,
    decimal FilledQuantity,
    OrderState State);

public sealed record PaperReconciliationFill(
    string Identity,
    string BrokerOrderId,
    string InstrumentIdentifier,
    decimal Quantity,
    decimal Price,
    DateTimeOffset BrokerExecutionTime);

public sealed record PaperReconciliationPosition(
    string InstrumentIdentifier,
    decimal Quantity,
    decimal MarketValue,
    string Currency);

public sealed record PaperReconciliationReservation(
    ExecutionIntentId IntentId,
    decimal RemainingAmount,
    ExposureReservationState State);

public sealed record PaperLocalReconciliationState(
    IReadOnlyList<PaperReconciliationOrder> Orders,
    IReadOnlyList<PaperReconciliationFill> Fills,
    IReadOnlyList<PaperReconciliationReservation> Reservations,
    int UnevaluatedFillCount = 0);

public sealed record PaperBrokerReconciliationSnapshot(
    IntentAccountBinding ObservedBinding,
    DateTimeOffset ObservedAt,
    decimal BuyingPower,
    string Currency,
    IReadOnlyList<PaperReconciliationOrder> Orders,
    IReadOnlyList<PaperReconciliationFill> Fills,
    IReadOnlyList<PaperReconciliationPosition> Positions,
    IReadOnlyList<PaperBrokerExecutionUpdate>? ExecutionUpdates = null);

public interface IPaperBrokerReconciliationAdapter
{
    ValueTask<PaperBrokerReconciliationSnapshot> ObserveAsync(
        IntentAccountBinding expectedBinding,
        CancellationToken cancellationToken);
}

public sealed class PaperBrokerReconciliationUnavailableException(string message)
    : InvalidOperationException(message);

public interface IPaperReconciliationPersistence
{
    ValueTask<IReadOnlyList<GoalVersionId>> FindRecoveryTargetsAsync(
        CancellationToken cancellationToken);

    ValueTask<PaperLocalReconciliationState> ReadAsync(
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        CancellationToken cancellationToken);
}

public sealed record PaperReconciliationRecoveryOutcome(
    int TargetCount,
    int CurrentCount,
    int LockedCount,
    int UnavailableCount);

public interface IPaperReconciliationRecovery
{
    ValueTask<PaperReconciliationRecoveryOutcome> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default);
}

public interface IPaperReconciliationRunner
{
    ValueTask<ReconciliationRecord> ReconcileAsync(
        GoalVersionId goalVersionId,
        OperatorId actorId,
        CancellationToken cancellationToken = default);
}

public sealed class PaperReconciliationRecoveryService(
    IPaperReconciliationPersistence persistence,
    PaperReconciliationService service) : IPaperReconciliationRecovery
{
    public async ValueTask<PaperReconciliationRecoveryOutcome> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var targets = await persistence.FindRecoveryTargetsAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = 0;
        var locked = 0;
        var unavailable = 0;
        foreach (var target in targets)
        {
            try
            {
                var result = await service.ReconcileAsync(target, actorId, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Result == ReconciliationResult.CURRENT)
                {
                    current++;
                }
                else
                {
                    locked++;
                }
            }
            catch (PaperBrokerReconciliationUnavailableException)
            {
                unavailable++;
            }
        }

        return new(targets.Count, current, locked, unavailable);
    }
}

public sealed class PaperReconciliationService(
    IGoalPersistence goalPersistence,
    IPaperReconciliationPersistence reconciliationPersistence,
    IExecutionSafetyPersistence safetyPersistence,
    IPaperBrokerReconciliationAdapter brokerAdapter,
    TimeProvider timeProvider,
    PaperBrokerEventService? eventService = null,
    IPaperSubmissionInterlock? submissionInterlock = null) : IPaperReconciliationRunner
{
    public async ValueTask<ReconciliationRecord> ReconcileAsync(
        GoalVersionId goalVersionId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        await using var submissionLease = submissionInterlock is null
            ? null
            : await submissionInterlock.EnterAsync(cancellationToken)
                .ConfigureAwait(false);
        var goal = await goalPersistence.FindGoalVersionAsync(goalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ExecutionSafetyException("The reconciliation Goal version does not exist.");
        var binding = ExecutionSafetyService.ToAccountBinding(goal);
        var reconciliationId = ReconciliationId.New();
        var startedAt = timeProvider.GetUtcNow();
        await safetyPersistence.RecordReconciliationStartedAsync(
            reconciliationId, goal.Id, binding, actorId, startedAt, cancellationToken)
            .ConfigureAwait(false);
        var local = await reconciliationPersistence.ReadAsync(goal.Id, binding, cancellationToken)
            .ConfigureAwait(false);
        var broker = await brokerAdapter.ObserveAsync(binding, cancellationToken)
            .ConfigureAwait(false);
        PaperReconciliationComparer.ValidateEvidence(local, broker);
        var corrections = new List<string>();
        foreach (var update in broker.ExecutionUpdates ?? [])
        {
            if (eventService is null)
            {
                throw new ExecutionSafetyException(
                    "Broker reconciliation correction is not configured.");
            }

            var ingestion = await eventService.IngestAsync(update, actorId, cancellationToken)
                .ConfigureAwait(false);
            if (ingestion.Disposition == PaperBrokerEventDisposition.APPLIED)
            {
                corrections.Add($"Applied Broker execution event {update.EventReference}.");
            }
        }

        if (corrections.Count != 0)
        {
            local = await reconciliationPersistence.ReadAsync(goal.Id, binding,
                cancellationToken).ConfigureAwait(false);
        }

        var record = PaperReconciliationComparer.Compare(reconciliationId, goal, local, broker,
            startedAt, timeProvider.GetUtcNow(), corrections);
        await safetyPersistence.InsertReconciliationAsync(record, actorId, cancellationToken)
            .ConfigureAwait(false);
        return record;
    }
}

public static class PaperReconciliationComparer
{
    public static ReconciliationRecord Compare(
        GoalVersion goal,
        PaperLocalReconciliationState local,
        PaperBrokerReconciliationSnapshot broker,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IEnumerable<string>? corrections = null) => Compare(
        ReconciliationId.New(), goal, local, broker, startedAt, completedAt, corrections);

    internal static ReconciliationRecord Compare(
        ReconciliationId reconciliationId,
        GoalVersion goal,
        PaperLocalReconciliationState local,
        PaperBrokerReconciliationSnapshot broker,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IEnumerable<string>? corrections = null)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(broker);
        var expected = ExecutionSafetyService.ToAccountBinding(goal);
        if (goal.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || expected.Environment != BrokerEnvironment.PAPER)
        {
            throw new ExecutionSafetyException(
                "Detailed reconciliation accepts only paper authority.");
        }

        ValidateEvidence(local, broker);
        var differences = new List<string>();
        if (broker.ObservedBinding != expected)
        {
            differences.Add("Observed Broker authority differs from the Goal binding.");
        }

        CompareOrders(local.Orders, broker.Orders, differences);
        CompareFills(local.Fills, broker.Fills, differences);
        ComparePositions(local, broker.Positions, differences);
        CompareReservations(local, differences);
        if (local.UnevaluatedFillCount != 0)
        {
            differences.Add(
                $"{local.UnevaluatedFillCount} local Fill records lack post-Fill safety evaluation.");
        }

        var positionExposure = broker.Positions.Sum(item => Math.Abs(item.MarketValue));
        var openOrderExposure = broker.Orders
            .Where(item => !IsTerminal(item.State))
            .Sum(item => Math.Abs((item.Quantity - item.FilledQuantity) * item.LimitPrice));
        var comparedOrderRange = EvidenceReference("orders", new
        {
            Local = local.Orders.OrderBy(item => item.BrokerClientOrderId, StringComparer.Ordinal),
            Broker = broker.Orders.OrderBy(item => item.BrokerClientOrderId, StringComparer.Ordinal)
        });
        var comparedFillRange = EvidenceReference("fills", new
        {
            Local = local.Fills.OrderBy(item => item.Identity, StringComparer.Ordinal),
            Broker = broker.Fills.OrderBy(item => item.Identity, StringComparer.Ordinal)
        });
        var comparedPositionSnapshot = EvidenceReference("positions", new
        {
            broker.ObservedAt,
            LocalOrders = local.Orders.OrderBy(
                item => item.BrokerClientOrderId, StringComparer.Ordinal),
            LocalFills = local.Fills.OrderBy(item => item.Identity, StringComparer.Ordinal),
            Broker = broker.Positions.OrderBy(
                item => item.InstrumentIdentifier, StringComparer.Ordinal)
        });
        var operatorAction = differences.Count == 0
            ? null
            : "Review and resolve the reconciliation difference before new execution.";
        return ReconciliationRecord.Create(reconciliationId, goal.Id,
            broker.ObservedBinding, goal.ExecutionBinding!.ReconciliationPolicyVersion,
            startedAt, completedAt, broker.ObservedAt, positionExposure,
            openOrderExposure, broker.BuyingPower, broker.Currency,
            comparedOrderRange, comparedFillRange, comparedPositionSnapshot,
            differences, corrections ?? [], operatorAction, observationSucceeded: true);
    }

    private static void CompareOrders(IReadOnlyList<PaperReconciliationOrder> local,
        IReadOnlyList<PaperReconciliationOrder> broker, List<string> differences)
    {
        var brokerByClient = broker.ToDictionary(item => item.BrokerClientOrderId,
            StringComparer.Ordinal);
        var localClients = local.Select(item => item.BrokerClientOrderId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var order in local.OrderBy(item => item.BrokerClientOrderId,
            StringComparer.Ordinal))
        {
            if (!brokerByClient.TryGetValue(order.BrokerClientOrderId, out var observed))
            {
                differences.Add($"Local Order {order.BrokerClientOrderId} is absent from Broker truth.");
                continue;
            }

            if (!string.Equals(order.BrokerOrderId, observed.BrokerOrderId,
                    StringComparison.Ordinal)
                || order.InstrumentIdentifier != observed.InstrumentIdentifier
                || order.Side != observed.Side || order.Quantity != observed.Quantity
                || order.LimitPrice != observed.LimitPrice
                || order.FilledQuantity != observed.FilledQuantity
                || order.State != observed.State)
            {
                differences.Add($"Order {order.BrokerClientOrderId} differs from Broker truth.");
            }
        }

        foreach (var order in broker.Where(item => !localClients.Contains(item.BrokerClientOrderId))
            .OrderBy(item => item.BrokerClientOrderId, StringComparer.Ordinal))
        {
            differences.Add($"Broker Order {order.BrokerClientOrderId} is external to Stockripper.");
        }
    }

    private static void CompareFills(IReadOnlyList<PaperReconciliationFill> local,
        IReadOnlyList<PaperReconciliationFill> broker, List<string> differences)
    {
        var brokerByIdentity = broker.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var localIdentities = local.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (var fill in local.OrderBy(item => item.Identity, StringComparer.Ordinal))
        {
            if (!brokerByIdentity.TryGetValue(fill.Identity, out var observed))
            {
                differences.Add($"Local Fill {fill.Identity} is absent from Broker truth.");
                continue;
            }

            if (fill.BrokerOrderId != observed.BrokerOrderId
                || fill.InstrumentIdentifier != observed.InstrumentIdentifier
                || fill.Quantity != observed.Quantity || fill.Price != observed.Price
                || fill.BrokerExecutionTime != observed.BrokerExecutionTime)
            {
                differences.Add($"Fill {fill.Identity} differs from Broker truth.");
            }
        }

        foreach (var fill in broker.Where(item => !localIdentities.Contains(item.Identity))
            .OrderBy(item => item.Identity, StringComparer.Ordinal))
        {
            differences.Add($"Broker Fill {fill.Identity} is absent from Stockripper.");
        }
    }

    private static void ComparePositions(PaperLocalReconciliationState local,
        IReadOnlyList<PaperReconciliationPosition> broker, List<string> differences)
    {
        var sideByBrokerOrder = local.Orders
            .Where(item => !string.IsNullOrWhiteSpace(item.BrokerOrderId))
            .ToDictionary(item => item.BrokerOrderId!, item => item.Side, StringComparer.Ordinal);
        foreach (var fill in local.Fills.Where(item =>
                !sideByBrokerOrder.ContainsKey(item.BrokerOrderId))
            .OrderBy(item => item.Identity, StringComparer.Ordinal))
        {
            differences.Add($"Local Fill {fill.Identity} has no matching local Order.");
        }

        var calculated = local.Fills
            .Where(item => sideByBrokerOrder.ContainsKey(item.BrokerOrderId))
            .GroupBy(item => item.InstrumentIdentifier, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Sum(fill => sideByBrokerOrder[fill.BrokerOrderId] == OrderSide.SELL
                    ? -fill.Quantity
                    : fill.Quantity), StringComparer.Ordinal);
        var observed = broker.ToDictionary(item => item.InstrumentIdentifier,
            item => item.Quantity, StringComparer.Ordinal);
        foreach (var instrument in calculated.Keys.Concat(observed.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            _ = calculated.TryGetValue(instrument, out var localQuantity);
            _ = observed.TryGetValue(instrument, out var brokerQuantity);
            if (localQuantity != brokerQuantity)
            {
                differences.Add($"Position {instrument} differs from calculated Fill truth.");
            }
        }
    }

    private static void CompareReservations(PaperLocalReconciliationState local,
        List<string> differences)
    {
        var reservations = local.Reservations.ToDictionary(item => item.IntentId);
        var orderIntents = local.Orders.Where(item => item.IntentId.HasValue)
            .Select(item => item.IntentId!.Value).ToHashSet();
        foreach (var order in local.Orders.Where(item => item.IntentId.HasValue)
            .OrderBy(item => item.BrokerClientOrderId, StringComparer.Ordinal))
        {
            var expected = IsTerminal(order.State)
                ? 0m
                : (order.Quantity - order.FilledQuantity) * order.LimitPrice;
            if (!reservations.TryGetValue(order.IntentId!.Value, out var reservation)
                || reservation.RemainingAmount != expected
                || (expected == 0m) != (reservation.State == ExposureReservationState.RELEASED))
            {
                differences.Add($"Exposure reservation for Order {order.BrokerClientOrderId} differs from expected exposure.");
            }
        }

        foreach (var reservation in local.Reservations.Where(item =>
                !orderIntents.Contains(item.IntentId)).OrderBy(item => item.IntentId.Value))
        {
            differences.Add($"Exposure reservation {reservation.IntentId.Value:D} has no matching Order.");
        }
    }

    internal static void ValidateEvidence(PaperLocalReconciliationState local,
        PaperBrokerReconciliationSnapshot broker)
    {
        ArgumentNullException.ThrowIfNull(local.Orders);
        ArgumentNullException.ThrowIfNull(local.Fills);
        ArgumentNullException.ThrowIfNull(local.Reservations);
        ArgumentNullException.ThrowIfNull(broker.ObservedBinding);
        ArgumentNullException.ThrowIfNull(broker.Orders);
        ArgumentNullException.ThrowIfNull(broker.Fills);
        ArgumentNullException.ThrowIfNull(broker.Positions);
        if (local.UnevaluatedFillCount < 0
            || broker.ObservedBinding.Environment != BrokerEnvironment.PAPER
            || broker.BuyingPower < 0m || broker.Currency.Length != 3
            || broker.Currency.Any(character => character is < 'A' or > 'Z')
            || local.Orders.Select(item => item.BrokerClientOrderId).Distinct(StringComparer.Ordinal).Count() != local.Orders.Count
            || local.Fills.Select(item => item.Identity).Distinct(StringComparer.Ordinal).Count() != local.Fills.Count
            || local.Reservations.Select(item => item.IntentId).Distinct().Count() != local.Reservations.Count
            || broker.Orders.Any(InvalidOrder)
            || broker.Fills.Any(item => string.IsNullOrWhiteSpace(item.Identity)
                || string.IsNullOrWhiteSpace(item.BrokerOrderId)
                || item.Quantity <= 0m || item.Price <= 0m)
            || broker.Positions.Any(item => string.IsNullOrWhiteSpace(item.InstrumentIdentifier)
                || item.MarketValue < 0m || item.Currency != broker.Currency)
            || broker.Orders.Select(item => item.BrokerClientOrderId).Distinct(StringComparer.Ordinal).Count() != broker.Orders.Count
            || broker.Fills.Select(item => item.Identity).Distinct(StringComparer.Ordinal).Count() != broker.Fills.Count
            || broker.Positions.Select(item => item.InstrumentIdentifier).Distinct(StringComparer.Ordinal).Count() != broker.Positions.Count)
        {
            throw new ExecutionSafetyException("Broker reconciliation evidence is invalid.");
        }
    }

    private static bool InvalidOrder(PaperReconciliationOrder item) =>
        string.IsNullOrWhiteSpace(item.BrokerClientOrderId)
        || (item.State != OrderState.REJECTED && string.IsNullOrWhiteSpace(item.BrokerOrderId))
        || string.IsNullOrWhiteSpace(item.InstrumentIdentifier)
        || item.Quantity <= 0m || item.LimitPrice <= 0m
        || item.FilledQuantity < 0m || item.FilledQuantity > item.Quantity
        || item.State is OrderState.CREATED or OrderState.SUBMITTING
            or OrderState.SUBMISSION_UNKNOWN or OrderState.RECONCILIATION_REQUIRED;

    private static bool IsTerminal(OrderState state) => state is OrderState.FILLED
        or OrderState.CANCELED or OrderState.REJECTED or OrderState.EXPIRED;

    private static string EvidenceReference(string kind, object evidence)
    {
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence)));
        return $"{kind}:sha256:{digest}";
    }
}
