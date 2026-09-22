using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresDatabaseMigrator(NpgsqlDataSource dataSource)
{
    public async ValueTask MigrateAsync(CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<StockripperDbContext>()
            .UseNpgsql(dataSource)
            .Options;
        await using var context = new StockripperDbContext(options);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
