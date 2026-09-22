using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;

namespace Stockripper.Infrastructure.Audit;

internal static class AuditChain
{
    public const string ChainId = "primary";
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string CreateCanonicalPayload(AuditEventData data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("schema", "stockripper-audit-event-v1");
        writer.WriteNumber("sequence", data.Sequence);
        writer.WriteString("eventId", data.EventId.ToString("D"));
        writer.WriteString("eventType", data.EventType.ToString());
        writer.WriteString("eventTime", data.EventTime.ToString("O"));
        writer.WriteString("actorId", data.ActorId.Value.ToString("D"));
        WriteNullableIdentifier(writer, "goalVersionId", data.GoalVersionId?.Value);
        WriteNullableIdentifier(writer, "sessionId", data.SessionId?.Value);
        writer.WriteStartObject("relatedRecordIdentifiers");
        foreach (var pair in data.RelatedRecordIdentifiers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        if (data.PreviousState is null)
        {
            writer.WriteNull("previousState");
        }
        else
        {
            writer.WriteString("previousState", data.PreviousState);
        }

        writer.WriteString("newState", data.NewState);
        writer.WriteString("reason", data.Reason);
        writer.WriteString("sourceReference", data.SourceReference);
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string ComputeHash(string previousHash, string canonicalPayload)
    {
        var previousBytes = Convert.FromHexString(previousHash);
        var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var input = new byte[previousBytes.Length + payloadBytes.Length];
        previousBytes.CopyTo(input, 0);
        payloadBytes.CopyTo(input, previousBytes.Length);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static void WriteNullableIdentifier(Utf8JsonWriter writer, string propertyName, Guid? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value.Value.ToString("D"));
        }
    }
}

internal sealed record AuditEventData(
    Guid EventId,
    long Sequence,
    AuditEventType EventType,
    DateTimeOffset EventTime,
    OperatorId ActorId,
    GoalVersionId? GoalVersionId,
    SessionId? SessionId,
    IReadOnlyDictionary<string, string> RelatedRecordIdentifiers,
    string? PreviousState,
    string NewState,
    string Reason,
    string SourceReference);
