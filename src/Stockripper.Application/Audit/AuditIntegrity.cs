namespace Stockripper.Application.Audit;

public interface IAuditIntegrityVerifier
{
    ValueTask<AuditIntegrityResult> VerifyAsync(CancellationToken cancellationToken = default);
}

public sealed record AuditIntegrityResult(
    bool IsValid,
    long VerifiedEventCount,
    string? FailureCode)
{
    public static AuditIntegrityResult Valid(long verifiedEventCount) => new(
        true,
        verifiedEventCount,
        null);

    public static AuditIntegrityResult Invalid(long verifiedEventCount, string failureCode) => new(
        false,
        verifiedEventCount,
        failureCode);
}

public sealed class AuditIntegrityException(string failureCode)
    : Exception("Audit integrity verification failed.")
{
    public string FailureCode { get; } = failureCode;
}

public sealed class AuditIntegrityGuard(IAuditIntegrityVerifier verifier)
{
    public async ValueTask VerifyOrThrowAsync(CancellationToken cancellationToken = default)
    {
        var result = await verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new AuditIntegrityException(result.FailureCode ?? "UNKNOWN");
        }
    }
}
