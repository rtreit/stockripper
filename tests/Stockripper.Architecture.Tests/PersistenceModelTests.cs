using Microsoft.EntityFrameworkCore;
using Stockripper.Infrastructure.Persistence;

namespace Stockripper.Architecture.Tests;

public sealed class PersistenceModelTests
{
    [Fact]
    public void Replay_foundation_entities_and_migration_are_registered()
    {
        var options = new DbContextOptionsBuilder<StockripperDbContext>()
            .UseNpgsql()
            .Options;
        using var context = new StockripperDbContext(options);

        Assert.NotNull(context.Model.FindEntityType(
            "Stockripper.Infrastructure.Persistence.DataSnapshotRow"));
        Assert.NotNull(context.Model.FindEntityType(
            "Stockripper.Infrastructure.Persistence.ReplayResultRow"));
        Assert.Contains("20260820183000_AddReplaySnapshots", context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
