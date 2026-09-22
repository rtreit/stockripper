using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using Stockripper.Application.Replay;
using Stockripper.Contracts;
using Stockripper.Domain.Decisions;
using Stockripper.Strategy.Contracts.V1;

namespace Stockripper.Infrastructure.Strategy;

public sealed record GrpcStrategyRuntimeOptions(
    TimeSpan Deadline,
    int MaximumResponseBytes,
    string AccessToken)
{
    public const string AuthenticationHeader = "x-stockripper-strategy-token";
}

public sealed class GrpcStrategyRuntime(
    StrategyRuntime.StrategyRuntimeClient client,
    GrpcStrategyRuntimeOptions options,
    TimeProvider timeProvider) : IStrategyRuntime
{
    public async ValueTask<StrategyRuntimeOutput> EvaluateAsync(
        StrategyRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        if (options.Deadline <= TimeSpan.Zero
            || options.MaximumResponseBytes <= 0
            || string.IsNullOrWhiteSpace(options.AccessToken))
        {
            throw new InvalidOperationException("The Strategy runtime limits are invalid.");
        }

        var message = new StrategyRequest
        {
            ContractVersion = ContractVersions.StrategyV1,
            StrategyVersion = request.StrategyVersion.Version,
            StrategyArtifactDigest = request.StrategyVersion.Digest,
            GoalVersionId = request.GoalVersionId.Value.ToString("D"),
            InputSnapshotId = request.DataSnapshotId.Value.ToString("D"),
            InputSnapshotDigest = request.DataSnapshotDigest,
            Seed = request.Seed
        };
        StrategyResponse response;
        try
        {
            var headers = new Metadata
            {
                { GrpcStrategyRuntimeOptions.AuthenticationHeader, options.AccessToken }
            };
            response = await client.EvaluateAsync(
                message,
                headers,
                deadline: timeProvider.GetUtcNow().UtcDateTime.Add(options.Deadline),
                cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException exception) when (exception.StatusCode is not StatusCode.Cancelled)
        {
            throw new StrategyRuntimeUnavailableException(
                "The Strategy runtime request failed.",
                exception);
        }

        if (response.CalculateSize() > options.MaximumResponseBytes)
        {
            throw new StrategyRuntimeUnavailableException("The Strategy response exceeds its size limit.");
        }

        var metrics = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var metric in response.OutputMetrics)
        {
            if (decimal.TryParse(
                    metric.Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                metrics.TryAdd(metric.Key, value);
            }
        }

        return new StrategyRuntimeOutput(
            response.ContractVersion,
            new Domain.Goals.VersionReference(
                request.StrategyVersion.Identifier,
                response.StrategyVersion,
                response.StrategyArtifactDigest),
            response.StrategyArtifactDigest,
            MapAction(response.Action),
            NullIfEmpty(response.InstrumentIdentifier),
            NullIfEmpty(response.Quantity),
            NullIfEmpty(response.LimitPrice),
            NullIfEmpty(response.Reason),
            NullIfEmpty(response.Confidence),
            metrics,
            response.Evidence);
    }

    private static DecisionAction MapAction(StrategyAction action) => action switch
    {
        StrategyAction.NoAction => DecisionAction.NO_ACTION,
        StrategyAction.Buy => DecisionAction.BUY,
        StrategyAction.Sell => DecisionAction.SELL,
        _ => DecisionAction.UNKNOWN
    };

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
