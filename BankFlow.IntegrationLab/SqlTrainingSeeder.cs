using System.Data;
using Microsoft.Data.SqlClient;

namespace BankFlow.IntegrationLab;

public sealed class SqlTrainingSeeder(string connectionString)
{
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "[learning].[SeedAccountSourceData]";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<ImportResult> RunEltAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var batchId = Guid.NewGuid();
        await using (var load = connection.CreateCommand())
        {
            load.CommandType = CommandType.StoredProcedure;
            load.CommandText = "[etl].[LoadSeededAccountSnapshotBatch]";
            load.Parameters.Add("@BatchId", SqlDbType.UniqueIdentifier).Value = batchId;
            await load.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var resultCommand = connection.CreateCommand();
        resultCommand.CommandText = """
            SELECT [Status], [ReadCount], [AcceptedCount], [RejectedCount], [InsertedCount], [UpdatedCount]
            FROM [etl].[ImportBatch]
            WHERE [BatchId] = @BatchId;
            """;
        resultCommand.Parameters.Add("@BatchId", SqlDbType.UniqueIdentifier).Value = batchId;
        await using var reader = await resultCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"ELT batch '{batchId}' disappeared after processing.");

        return new ImportResult(batchId, reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
    }
}
