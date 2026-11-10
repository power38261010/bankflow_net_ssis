using System.Data;
using Microsoft.Data.SqlClient;

namespace BankFlow.IntegrationLab;

public sealed record ImportResult(
    Guid BatchId,
    string Status,
    int ReadCount,
    int AcceptedCount,
    int RejectedCount,
    int InsertedCount,
    int UpdatedCount);

public sealed class AccountSnapshotImporter(string connectionString)
{
    private readonly AccountSnapshotCsv _csv = new();

    public async Task<ImportResult> ImportCsvAsync(
        TextReader input,
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        var rows = _csv.Read(input, cancellationToken);
        var batchId = Guid.NewGuid();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var createBatch = connection.CreateCommand())
        {
            createBatch.CommandText = """
                INSERT INTO [etl].[ImportBatch] ([BatchId], [SourceSystem], [SourceFile], [Status], [StartedAtUtc])
                VALUES (@BatchId, N'CSV', @SourceFile, N'Loading', SYSUTCDATETIME());
                """;
            createBatch.Parameters.Add("@BatchId", SqlDbType.UniqueIdentifier).Value = batchId;
            createBatch.Parameters.Add("@SourceFile", SqlDbType.NVarChar, 260).Value = sourceFile;
            await createBatch.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            using (var bulkCopy = new SqlBulkCopy(connection))
            {
                bulkCopy.DestinationTableName = "[etl].[AccountSnapshotStage]";
                bulkCopy.BatchSize = 1000;
                bulkCopy.BulkCopyTimeout = 120;
                foreach (var column in new[]
                         {
                             "BatchId", "RowNumber", "SourceSystem", "SourceAccountId", "CustomerName",
                             "AccountNumber", "Balance", "SnapshotDate", "ValidationError", "RawData"
                         })
                    bulkCopy.ColumnMappings.Add(column, column);

                await bulkCopy.WriteToServerAsync(ToDataTable(batchId, rows), cancellationToken);
            }

            await using (var process = connection.CreateCommand())
            {
                process.CommandType = CommandType.StoredProcedure;
                process.CommandText = "[etl].[ProcessAccountSnapshotBatch]";
                process.Parameters.Add("@BatchId", SqlDbType.UniqueIdentifier).Value = batchId;
                await process.ExecuteNonQueryAsync(cancellationToken);
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
                throw new InvalidOperationException($"Import batch '{batchId}' disappeared after processing.");

            return new ImportResult(batchId, reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
        }
        catch (Exception exception)
        {
            await using var fail = connection.CreateCommand();
            fail.CommandText = """
                UPDATE [etl].[ImportBatch]
                SET [Status] = N'Failed', [CompletedAtUtc] = SYSUTCDATETIME(), [ErrorMessage] = @Error
                WHERE [BatchId] = @BatchId AND [Status] = N'Loading';
                """;
            fail.Parameters.Add("@BatchId", SqlDbType.UniqueIdentifier).Value = batchId;
            fail.Parameters.Add("@Error", SqlDbType.NVarChar, 2000).Value = exception.Message;
            await fail.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }
    }

    private static DataTable ToDataTable(Guid batchId, IReadOnlyList<AccountSnapshotRow> rows)
    {
        var table = new DataTable();
        table.Columns.Add("BatchId", typeof(Guid));
        table.Columns.Add("RowNumber", typeof(int));
        table.Columns.Add("SourceSystem", typeof(string));
        table.Columns.Add("SourceAccountId", typeof(string));
        table.Columns.Add("CustomerName", typeof(string));
        table.Columns.Add("AccountNumber", typeof(string));
        table.Columns.Add("Balance", typeof(decimal));
        table.Columns.Add("SnapshotDate", typeof(DateTime));
        table.Columns.Add("ValidationError", typeof(string));
        table.Columns.Add("RawData", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(batchId, row.RowNumber, Db(row.SourceSystem), Db(row.SourceAccountId),
                Db(row.CustomerName), Db(row.AccountNumber), Db(row.Balance),
                row.SnapshotDate.HasValue ? row.SnapshotDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                Db(row.ValidationError), row.RawData);
        }

        return table;
    }

    private static object Db<T>(T? value) where T : class => value is null ? DBNull.Value : value;

    private static object Db(decimal? value) => value.HasValue ? value.Value : DBNull.Value;
}
