using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;

namespace Stockripper.Domain.Execution;

public enum ReviewResult
{
    APPROVED,
    REJECTED
}

public sealed record ReviewApproval
{
    private const string DigestSchema = "stockripper-review-approval-v1";

    private ReviewApproval(
        ReviewApprovalId id,
        ExecutionIntentId intentId,
        OperatorId operatorId,
        ReviewResult result,
        DateTimeOffset actionTime,
        string intentDigest,
        DateTimeOffset expiresAt,
        string? operatorNote,
        string contentDigest)
    {
        Id = id;
        IntentId = intentId;
        OperatorId = operatorId;
        Result = result;
        ActionTime = actionTime;
        IntentDigest = intentDigest;
        ExpiresAt = expiresAt;
        OperatorNote = operatorNote;
        ContentDigest = contentDigest;
    }

    public ReviewApprovalId Id { get; }
    public ExecutionIntentId IntentId { get; }
    public OperatorId OperatorId { get; }
    public ReviewResult Result { get; }
    public DateTimeOffset ActionTime { get; }
    public string IntentDigest { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string? OperatorNote { get; }
    public string ContentDigest { get; }

    public static ReviewApproval Create(
        ReviewApprovalId id,
        ExecutionIntentId intentId,
        OperatorId operatorId,
        ReviewResult result,
        DateTimeOffset actionTime,
        string intentDigest,
        DateTimeOffset expiresAt,
        string? operatorNote)
    {
        var action = UtcTimestamp.Require(actionTime, nameof(actionTime));
        var expiry = UtcTimestamp.Require(expiresAt, nameof(expiresAt));
        if (expiry <= action)
        {
            throw new DomainRuleViolationException("The review expiry must be after the action time.");
        }

        if (string.IsNullOrWhiteSpace(intentDigest)
            || intentDigest.Length != 64
            || intentDigest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new DomainRuleViolationException("The review needs a valid intent digest.");
        }

        var note = string.IsNullOrWhiteSpace(operatorNote) ? null : operatorNote.Trim();
        var digest = ComputeDigest(
            id,
            intentId,
            operatorId,
            result,
            action,
            intentDigest,
            expiry,
            note);
        return new ReviewApproval(
            id,
            intentId,
            operatorId,
            result,
            action,
            intentDigest,
            expiry,
            note,
            digest);
    }

    public static ReviewApproval Restore(
        ReviewApprovalId id,
        ExecutionIntentId intentId,
        OperatorId operatorId,
        ReviewResult result,
        DateTimeOffset actionTime,
        string intentDigest,
        DateTimeOffset expiresAt,
        string? operatorNote,
        string contentDigest)
    {
        var restored = Create(
            id,
            intentId,
            operatorId,
            result,
            actionTime,
            intentDigest,
            expiresAt,
            operatorNote);
        if (!string.Equals(restored.ContentDigest, contentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored Review approval integrity check failed.");
        }

        return restored;
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(
            Id,
            IntentId,
            OperatorId,
            Result,
            ActionTime,
            IntentDigest,
            ExpiresAt,
            OperatorNote),
        StringComparison.Ordinal);

    private static string ComputeDigest(
        ReviewApprovalId id,
        ExecutionIntentId intentId,
        OperatorId operatorId,
        ReviewResult result,
        DateTimeOffset actionTime,
        string intentDigest,
        DateTimeOffset expiresAt,
        string? operatorNote)
    {
        var value = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            IntentId = intentId.Value,
            OperatorId = operatorId.Value,
            Result = result.ToString(),
            ActionTime = actionTime.ToString("O"),
            IntentDigest = intentDigest,
            ExpiresAt = expiresAt.ToString("O"),
            OperatorNote = operatorNote
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }
}
