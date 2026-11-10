using BankFlow.IntegrationLab;
using Microsoft.Data.SqlClient;

namespace BankFlow.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class EtlSqlServerFactAttribute : FactAttribute
{
    public EtlSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BANKFLOW_ETL_TEST_SQL")))
            Skip = "Set BANKFLOW_ETL_TEST_SQL to a disposable SQL Server admin connection to run ETL integration tests.";
    }
}

[CollectionDefinition("ETL SQL integration", DisableParallelization = true)]
public sealed class EtlSqlServerCollection
{
}

[Collection("ETL SQL integration")]
public sealed class SqlAccountSnapshotEtlTests
{
    [EtlSqlServerFact]
    public async Task Import_ShouldBeIdempotentAndPersistRejectionsAndAudit()
    {
        var testConnection = Environment.GetEnvironmentVariable("BANKFLOW_ETL_TEST_SQL");

        var databaseName = "BankFlow_EtlTests_" + Guid.NewGuid().ToString("N");
        var admin = new SqlConnectionStringBuilder(testConnection) { InitialCatalog = "master" };
        var database = new SqlConnectionStringBuilder(testConnection) { InitialCatalog = databaseName };

        await using (var connection = new SqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{databaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await SqlScriptRunner.RunDirectoryAsync(database.ConnectionString, Path.Combine(AppContext.BaseDirectory, "Sql"));
            var importer = new AccountSnapshotImporter(database.ConnectionString);
            var csv = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "account-snapshots.csv"));
            var first = await importer.ImportCsvAsync(new StringReader(csv), "synthetic.csv");
            var second = await importer.ImportCsvAsync(new StringReader(csv), "synthetic.csv");

            Assert.Equal("Completed", first.Status);
            Assert.Equal(5, first.ReadCount);
            Assert.Equal(4, first.AcceptedCount);
            Assert.Equal(1, first.RejectedCount);
            Assert.Equal(4, first.InsertedCount);
            Assert.Equal(0, first.UpdatedCount);
            Assert.Equal(0, second.InsertedCount);
            Assert.Equal(0, second.UpdatedCount);
            Assert.Equal(1, second.RejectedCount);

            await using var verify = new SqlConnection(database.ConnectionString);
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM [learning].[AccountSnapshot]),
                    (SELECT COUNT(*) FROM [learning].[AccountSnapshotAudit]),
                    (SELECT COUNT(*) FROM [etl].[RejectedRow]);
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(4, reader.GetInt32(0));
            Assert.Equal(4, reader.GetInt32(1));
            Assert.Equal(2, reader.GetInt32(2));
        }
        finally
        {
            await using var connection = new SqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
            await drop.ExecuteNonQueryAsync();
        }
    }

    [EtlSqlServerFact]
    public async Task SeededElt_ShouldBeRepeatableAndExposeCuratedResultsThroughSqlObjects()
    {
        var testConnection = Environment.GetEnvironmentVariable("BANKFLOW_ETL_TEST_SQL")!;
        var databaseName = "BankFlow_EtlTests_" + Guid.NewGuid().ToString("N");
        var admin = new SqlConnectionStringBuilder(testConnection) { InitialCatalog = "master" };
        var database = new SqlConnectionStringBuilder(testConnection) { InitialCatalog = databaseName };

        await CreateDatabaseAsync(admin, databaseName);
        try
        {
            var sqlDirectory = Path.Combine(AppContext.BaseDirectory, "Sql");
            await SqlScriptRunner.RunDirectoryAsync(database.ConnectionString, sqlDirectory);
            await SqlScriptRunner.RunDirectoryAsync(database.ConnectionString, sqlDirectory);

            var seeder = new SqlTrainingSeeder(database.ConnectionString);
            Assert.Equal(8, await seeder.SeedAsync());
            Assert.Equal(8, await seeder.SeedAsync());

            var first = await seeder.RunEltAsync();
            var replay = await seeder.RunEltAsync();
            Assert.Equal("Completed", first.Status);
            Assert.Equal(8, first.ReadCount);
            Assert.Equal(6, first.AcceptedCount);
            Assert.Equal(2, first.RejectedCount);
            Assert.Equal(6, first.InsertedCount);
            Assert.Equal(0, first.UpdatedCount);
            Assert.Equal(0, replay.InsertedCount);
            Assert.Equal(0, replay.UpdatedCount);
            Assert.Equal(2, replay.RejectedCount);

            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM [source].[AccountSnapshotRaw]),
                    (SELECT COUNT(*) FROM [learning].[AccountSnapshot]),
                    (SELECT COUNT(*) FROM [learning].[AccountSnapshotAudit]),
                    (SELECT COUNT(*) FROM [etl].[RejectedRow]),
                    (SELECT COUNT(*) FROM [learning].[AccountSnapshotsBySource](N'Sybase', '2026-10-01', '2026-10-01')),
                    (SELECT COUNT(*) FROM [learning].[AccountExposureBySource] WHERE [SourceSystem] = N'Teradata');
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(8, reader.GetInt32(0));
            Assert.Equal(6, reader.GetInt32(1));
            Assert.Equal(6, reader.GetInt32(2));
            Assert.Equal(4, reader.GetInt32(3));
            Assert.Equal(2, reader.GetInt32(4));
            Assert.Equal(1, reader.GetInt32(5));
        }
        finally
        {
            await DropDatabaseAsync(admin, databaseName);
        }
    }

    [EtlSqlServerFact]
    public async Task SeededElt_ShouldRollbackStageAndCuratedChangesWhenTheTargetTriggerFails()
    {
        var testConnection = Environment.GetEnvironmentVariable("BANKFLOW_ETL_TEST_SQL")!;
        var databaseName = "BankFlow_EtlTests_" + Guid.NewGuid().ToString("N");
        var admin = new SqlConnectionStringBuilder(testConnection) { InitialCatalog = "master" };
        var database = new SqlConnectionStringBuilder(testConnection) { InitialCatalog = databaseName };

        await CreateDatabaseAsync(admin, databaseName);
        try
        {
            await SqlScriptRunner.RunDirectoryAsync(database.ConnectionString, Path.Combine(AppContext.BaseDirectory, "Sql"));
            await new SqlTrainingSeeder(database.ConnectionString).SeedAsync();

            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var createTrigger = connection.CreateCommand();
                createTrigger.CommandText = """
                    CREATE TRIGGER [learning].[TR_Training_ForceEtlFailure]
                    ON [learning].[AccountSnapshot]
                    AFTER INSERT, UPDATE
                    AS
                    BEGIN
                        THROW 51020, 'Injected ELT target failure.', 1;
                    END;
                    """;
                await createTrigger.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<SqlException>(() =>
                new SqlTrainingSeeder(database.ConnectionString).RunEltAsync());
            Assert.Contains("Injected ELT target failure", exception.Message);

            await using var verify = new SqlConnection(database.ConnectionString);
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM [etl].[ImportBatch] WHERE [Status] = N'Failed'),
                    (SELECT COUNT(*) FROM [etl].[AccountSnapshotStage]),
                    (SELECT COUNT(*) FROM [learning].[AccountSnapshot]),
                    (SELECT COUNT(*) FROM [learning].[AccountSnapshotAudit]);
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(0, reader.GetInt32(3));
        }
        finally
        {
            await DropDatabaseAsync(admin, databaseName);
        }
    }

    private static async Task CreateDatabaseAsync(SqlConnectionStringBuilder admin, string databaseName)
    {
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(SqlConnectionStringBuilder admin, string databaseName)
    {
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
    }
}
