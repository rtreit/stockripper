using System.Text;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class DataSnapshotTests
{
    [Fact]
    public void Same_content_produces_the_same_valid_digests()
    {
        var id = DataSnapshotId.New();
        var first = Create(id, "{\"value\":1}");
        var second = Create(id, "{\"value\":1}");

        Assert.Equal(first.PayloadDigest, second.PayloadDigest);
        Assert.Equal(first.ContentDigest, second.ContentDigest);
        Assert.True(first.HasValidContentDigest());
    }

    [Fact]
    public void Payload_change_produces_a_different_digest()
    {
        var id = DataSnapshotId.New();

        var first = Create(id, "{\"value\":1}");
        var second = Create(id, "{\"value\":2}");

        Assert.NotEqual(first.PayloadDigest, second.PayloadDigest);
        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public void Invalid_time_range_and_duplicate_sources_are_rejected()
    {
        Assert.Throws<DomainRuleViolationException>(() => DataSnapshot.Create(
            DataSnapshotId.New(),
            Version("data-set"),
            Version("data-policy"),
            "schema-v1",
            GoalTestData.ReferenceTime,
            GoalTestData.ReferenceTime.AddMinutes(-1),
            GoalTestData.ReferenceTime,
            [new DataSourceObservation("fixture", "1", GoalTestData.ReferenceTime)],
            [1]));
        Assert.Throws<DomainRuleViolationException>(() => DataSnapshot.Create(
            DataSnapshotId.New(),
            Version("data-set"),
            Version("data-policy"),
            "schema-v1",
            GoalTestData.ReferenceTime.AddMinutes(-1),
            GoalTestData.ReferenceTime,
            GoalTestData.ReferenceTime,
            [
                new DataSourceObservation("fixture", "1", GoalTestData.ReferenceTime),
                new DataSourceObservation("fixture", "2", GoalTestData.ReferenceTime)
            ],
            [1]));
    }

    private static DataSnapshot Create(DataSnapshotId id, string payload) => DataSnapshot.Create(
        id,
        Version("data-set"),
        Version("data-policy"),
        "schema-v1",
        GoalTestData.ReferenceTime.AddMinutes(-1),
        GoalTestData.ReferenceTime,
        GoalTestData.ReferenceTime,
        [new DataSourceObservation("fixture", "1", GoalTestData.ReferenceTime)],
        Encoding.UTF8.GetBytes(payload));

    private static VersionReference Version(string identifier) => new(identifier, "1", $"digest-{identifier}-1");
}
