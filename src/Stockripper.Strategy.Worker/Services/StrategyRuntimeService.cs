using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Stockripper.Contracts;
using Stockripper.Strategy.Contracts.V1;

namespace Stockripper.Strategy.Worker.Services;

public sealed class StrategyRuntimeService(IConfiguration configuration)
    : StrategyRuntime.StrategyRuntimeBase
{
    private const string AuthenticationHeader = "x-stockripper-strategy-token";

    public override Task<StrategyResponse> Evaluate(
        StrategyRequest request,
        ServerCallContext context)
    {
        ValidateAuthentication(context);
        if (!string.Equals(request.ContractVersion, ContractVersions.StrategyV1, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The contract version is unsupported."));
        }

        var response = new StrategyResponse
        {
            Action = StrategyAction.NoAction,
            Reason = "Phase 0 does not produce an executable action.",
            ContractVersion = request.ContractVersion,
            StrategyVersion = request.StrategyVersion,
            StrategyArtifactDigest = request.StrategyArtifactDigest,
            Evidence = $"NO_ACTION:{request.InputSnapshotDigest}:{request.Seed}"
        };
        response.OutputMetrics.Add("decisionCount", 1m.ToString(CultureInfo.InvariantCulture));
        response.OutputMetrics.Add("noActionCount", 1m.ToString(CultureInfo.InvariantCulture));
        return Task.FromResult(response);
    }

    private void ValidateAuthentication(ServerCallContext context)
    {
        var configuredToken = configuration["Strategy:AccessToken"];
        var suppliedToken = context.RequestHeaders
            .FirstOrDefault(header => string.Equals(
                header.Key,
                AuthenticationHeader,
                StringComparison.Ordinal))
            ?.Value;
        if (string.IsNullOrWhiteSpace(configuredToken)
            || string.IsNullOrWhiteSpace(suppliedToken))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication failed."));
        }

        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        if (configuredBytes.Length != suppliedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(configuredBytes, suppliedBytes))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication failed."));
        }
    }
}
