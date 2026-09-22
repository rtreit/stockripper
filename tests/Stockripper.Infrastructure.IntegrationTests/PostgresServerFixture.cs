using System.Diagnostics;
using System.Globalization;
using System.Net;
using Npgsql;
using Stockripper.Infrastructure.Audit;
using Stockripper.Infrastructure.Persistence;
using Stockripper.Testing;

namespace Stockripper.Infrastructure.IntegrationTests;

public sealed class PostgresServerFixture : IAsyncLifetime
{
    private NpgsqlDataSource? adminDataSource;
    private string? containerId;

    public async Task InitializeAsync()
    {
        var builder = CreateAdminConnectionBuilder();
        containerId = RequireEnvironment("STOCKRIPPER_TEST_POSTGRES_CONTAINER");
        if (!IsContainerId(containerId))
        {
            throw new InvalidOperationException("The PostgreSQL test container identity is invalid.");
        }

        adminDataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var connection = await adminDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        var versionText = (string?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The PostgreSQL version is unavailable.");
        var version = int.Parse(versionText, CultureInfo.InvariantCulture);
        if (version is < 180000 or >= 190000)
        {
            throw new InvalidOperationException("The integration tests require PostgreSQL 18.");
        }
    }

    public async Task DisposeAsync()
    {
        if (adminDataSource is not null)
        {
            await adminDataSource.DisposeAsync();
        }
    }

    public async ValueTask<PostgresTestDatabase> CreateDatabaseAsync()
    {
        var dataSource = adminDataSource
            ?? throw new InvalidOperationException("The PostgreSQL fixture is not initialized.");
        var databaseName = $"stockripper_test_{Guid.NewGuid():N}";
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"CREATE DATABASE {QuoteTestDatabase(databaseName)}",
            connection))
        {
            _ = await command.ExecuteNonQueryAsync();
        }

        var builder = CreateAdminConnectionBuilder();
        builder.Database = databaseName;
        builder.Pooling = true;
        var database = new PostgresTestDatabase(dataSource, databaseName, builder.ConnectionString);
        await database.InitializeAsync();
        return database;
    }

    public async ValueTask RestartServerAsync()
    {
        if (containerId is null || !IsContainerId(containerId))
        {
            throw new InvalidOperationException("The PostgreSQL test container identity is invalid.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("restart");
        startInfo.ArgumentList.Add(containerId);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Docker restart process did not start.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("The PostgreSQL test container did not restart.");
        }

        NpgsqlConnection.ClearAllPools();
        await WaitForServerAsync();
    }

    private async ValueTask WaitForServerAsync()
    {
        var dataSource = adminDataSource
            ?? throw new InvalidOperationException("The PostgreSQL fixture is not initialized.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                _ = await command.ExecuteScalarAsync();
                return;
            }
            catch (NpgsqlException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        throw new TimeoutException("PostgreSQL did not become ready after restart.");
    }

    private static NpgsqlConnectionStringBuilder CreateAdminConnectionBuilder()
    {
        var host = RequireEnvironment("STOCKRIPPER_TEST_POSTGRES_HOST");
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("The PostgreSQL test host must be a loopback address.");
        }

        var database = RequireEnvironment("STOCKRIPPER_TEST_POSTGRES_DATABASE");
        var username = RequireEnvironment("STOCKRIPPER_TEST_POSTGRES_USER");
        if (!string.Equals(database, "postgres", StringComparison.Ordinal)
            || !string.Equals(username, "stockripper_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The PostgreSQL test identity is invalid.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = int.Parse(
                RequireEnvironment("STOCKRIPPER_TEST_POSTGRES_PORT"),
                CultureInfo.InvariantCulture),
            Database = database,
            Username = username,
            Password = RequireEnvironment("STOCKRIPPER_TEST_POSTGRES_PASSWORD"),
            IncludeErrorDetail = false,
            Pooling = false
        };
    }

    private static string QuoteTestDatabase(string databaseName)
    {
        if (!databaseName.StartsWith("stockripper_test_", StringComparison.Ordinal)
            || databaseName.Any(character => character != '_' && !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidOperationException("The PostgreSQL test database name is invalid.");
        }

        return $"\"{databaseName}\"";
    }

    private static bool IsContainerId(string value) => value.Length is >= 12 and <= 64
        && value.All(char.IsAsciiHexDigit);

    private static string RequireEnvironment(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"The required test setting {name} is missing.");
}

public sealed class PostgresTestDatabase(
    NpgsqlDataSource adminDataSource,
    string databaseName,
    string databaseConnectionString) : IAsyncDisposable
{
    private NpgsqlDataSource dataSource = NpgsqlDataSource.Create(databaseConnectionString);

    public NpgsqlDataSource DataSource => dataSource;

    public PostgresGoalPersistence GoalPersistence => new(dataSource);

    public PostgresDataSnapshotPersistence SnapshotPersistence => new(dataSource);

    public PostgresGoalActivationPersistence ActivationPersistence => new(dataSource);

    public PostgresAccountAllocationPersistence AccountAllocationPersistence => new(dataSource);

    public PostgresSessionPersistence SessionPersistence => new(dataSource);

    public PostgresExecutablePreflightPersistence ExecutablePreflightPersistence => new(dataSource);

    public PostgresDecisionPersistence DecisionPersistence => new(dataSource);

    public PostgresPaperReviewIntentPersistence PaperReviewIntentPersistence => new(dataSource);

    public PostgresExecutionSafetyPersistence ExecutionSafetyPersistence => new(dataSource);

    public PostgresPaperOrderPersistence PaperOrderPersistence => new(dataSource);

    public PostgresPaperBrokerEventPersistence PaperBrokerEventPersistence => new(dataSource);

    public PostgresPaperPostFillSafetyPersistence PaperPostFillSafetyPersistence =>
        new(dataSource, new FixedPaperRiskPolicyProvider());

    public PostgresPaperCancellationPersistence PaperCancellationPersistence => new(dataSource);

    public PostgresResearchPersistence ResearchPersistence => new(dataSource);

    public PostgresReplayPersistence ReplayPersistence => new(dataSource);

    public PostgresAuditIntegrityVerifier AuditVerifier => new(dataSource);

    public async ValueTask InitializeAsync()
    {
        await new PostgresDatabaseMigrator(dataSource).MigrateAsync();
    }

    public async ValueTask ReopenApplicationAsync()
    {
        await dataSource.DisposeAsync();
        dataSource = NpgsqlDataSource.Create(databaseConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await dataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        if (!databaseName.StartsWith("stockripper_test_", StringComparison.Ordinal)
            || databaseName.Any(character => character != '_' && !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidOperationException("The PostgreSQL test database name is invalid.");
        }

        await using var connection = await adminDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE \"{databaseName}\" WITH (FORCE)",
            connection);
        _ = await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresServerFixture>
{
    public const string Name = "PostgreSQL";
}
