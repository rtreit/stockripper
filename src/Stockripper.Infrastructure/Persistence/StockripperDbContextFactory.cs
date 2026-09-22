using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stockripper.Infrastructure.Persistence;

public sealed class StockripperDbContextFactory : IDesignTimeDbContextFactory<StockripperDbContext>
{
    public StockripperDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StockripperDbContext>()
            .UseNpgsql()
            .Options;
        return new StockripperDbContext(options);
    }
}
