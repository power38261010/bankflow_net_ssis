using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using BankFlow.Domain;
using BankFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

public static class ReadLoadScenario
{
    public static async Task RunAsync()
    {
        var configured = Environment.GetEnvironmentVariable("BANKFLOW_TEST_SQL");
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("Set BANKFLOW_TEST_SQL to a local test server, never production.");
        var connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "BankFlow_ReadLoad_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<BankFlowDbContext>().UseSqlServer(connection).Options;
        var accountId = Guid.NewGuid();
        using var process = Process.GetCurrentProcess();
        var samples = new List<LoadResult>();
        await using var setup = new BankFlowDbContext(options);
        try
        {
            await setup.Database.MigrateAsync();
            setup.Accounts.Add(new Account(accountId, "Synthetic owner", "LOADSOURCE", "load-owner"));
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlRawAsync("""
                WITH numbers AS (SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS number FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO Accounts (Id, CustomerName, AccountNumber, OwnerUserId, Balance, CreatedAt)
                SELECT NEWID(), N'Synthetic owner', CONCAT(N'LOAD', FORMAT(number, '000000')), N'load-owner', 1, DATEADD(second, -CONVERT(int,number), SYSUTCDATETIME()) FROM numbers;
                WITH numbers AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS number FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO Transactions (Id, AccountId, Amount, Type, Description, CreatedAt)
                SELECT NEWID(), (SELECT Id FROM Accounts WHERE AccountNumber=N'LOADSOURCE'), 1, N'Deposit', N'Synthetic load fixture', DATEADD(second, -CONVERT(int,number), SYSUTCDATETIME()) FROM numbers;
                """);
            setup.ChangeTracker.Clear();
            Console.WriteLine($"Read-load fixture: 5001 accounts, 2000 movements. PID {Environment.ProcessId}.");
            var reportDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("BANKFLOW_LOAD_REPORT") ?? "BenchmarkDotNet.Artifacts/read-load");
            Directory.CreateDirectory(reportDirectory);
            await CapturePlanAsync(setup, accountId, reportDirectory);

            foreach (var operation in new[] { "legacy_accounts", "paged_accounts", "legacy_movements", "paged_movements" })
            {
                await ExecuteAsync(options, accountId, operation);
                var timings = new ConcurrentBag<double>();
                var elapsed = Stopwatch.StartNew();
                var allocations = GC.GetTotalAllocatedBytes(true);
                var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                var cpu = process.TotalProcessorTime;
                using var stop = new CancellationTokenSource();
                var runtime = new RuntimeSample();
                var sampler = SampleRuntimeAsync(runtime, stop.Token);
                await Parallel.ForEachAsync(Enumerable.Range(0, 100), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (_, token) =>
                {
                    var started = Stopwatch.GetTimestamp();
                    await ExecuteAsync(options, accountId, operation, token);
                    timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                });
                stop.Cancel();
                await sampler;
                elapsed.Stop();
                var ordered = timings.Order().ToArray();
                var sample = new LoadResult(operation, ordered.Length, 4, elapsed.Elapsed.TotalSeconds,
                    ordered[(int)Math.Ceiling(ordered.Length * 0.50) - 1], ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1],
                    GC.GetTotalAllocatedBytes(true) - allocations,
                    GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2],
                    (process.TotalProcessorTime - cpu).TotalMilliseconds, runtime.MaxThreadPoolThreads,
                    runtime.MaxThreadPoolQueue, runtime.MaxHeapBytes, runtime.MaxLohBytes);
                samples.Add(sample);
                Console.WriteLine(JsonSerializer.Serialize(sample));
            }
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "read-load.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Report directory: " + reportDirectory);
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    private static async Task ExecuteAsync(DbContextOptions<BankFlowDbContext> options, Guid accountId, string operation, CancellationToken cancellationToken = default)
    {
        await using var db = new BankFlowDbContext(options);
        var queries = new SqlBankingReadQueries(db);
        object result = operation switch
        {
            "legacy_accounts" => await db.Accounts.AsNoTracking().Where(account => account.OwnerUserId == "load-owner").ToListAsync(cancellationToken),
            "paged_accounts" => await queries.AccountsAsync(1, 20, null, "load-owner", cancellationToken),
            "legacy_movements" => await db.Transactions.AsNoTracking().Where(movement => movement.AccountId == accountId).ToListAsync(cancellationToken),
            "paged_movements" => (await queries.MovementsAsync(accountId, 1, 20, "load-owner", cancellationToken))!,
            _ => throw new ArgumentException("Unknown scenario.")
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, result.GetType());
        GC.KeepAlive(bytes);
    }

    private static async Task SampleRuntimeAsync(RuntimeSample sample, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            do
            {
                var memory = GC.GetGCMemoryInfo();
                sample.MaxThreadPoolThreads = Math.Max(sample.MaxThreadPoolThreads, ThreadPool.ThreadCount);
                sample.MaxThreadPoolQueue = Math.Max(sample.MaxThreadPoolQueue, ThreadPool.PendingWorkItemCount);
                sample.MaxHeapBytes = Math.Max(sample.MaxHeapBytes, memory.HeapSizeBytes);
                if (memory.GenerationInfo.Length > 3) sample.MaxLohBytes = Math.Max(sample.MaxLohBytes, memory.GenerationInfo[3].SizeAfterBytes);
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task CapturePlanAsync(BankFlowDbContext db, Guid accountId, string directory)
    {
        var query = db.Transactions.AsNoTracking().Where(movement => movement.AccountId == accountId)
            .OrderByDescending(movement => movement.CreatedAt).ThenByDescending(movement => movement.Id).Take(20)
            .Select(movement => new { movement.Id, movement.Amount, movement.Type, movement.Description, movement.CreatedAt, movement.TransferId });
        var sql = query.ToQueryString();
        await File.WriteAllTextAsync(Path.Combine(directory, "movement-page.sql"), sql);
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS XML ON;\n" + sql;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase) && await reader.ReadAsync())
                {
                    var plan = reader.GetString(0);
                    await File.WriteAllTextAsync(Path.Combine(directory, "movement-page.sqlplan"), plan);
                    var document = XDocument.Parse(plan);
                    XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                    Console.WriteLine("Movement plan operators: " + string.Join(", ", document.Descendants(ns + "RelOp").Select(element => element.Attribute("PhysicalOp")?.Value)));
                }
                else while (await reader.ReadAsync()) { }
            } while (await reader.NextResultAsync());
        }
        command.CommandText = "SET STATISTICS XML OFF";
        await command.ExecuteNonQueryAsync();
        await db.Database.CloseConnectionAsync();
    }

    private sealed class RuntimeSample
    {
        public int MaxThreadPoolThreads;
        public long MaxThreadPoolQueue;
        public long MaxHeapBytes;
        public long MaxLohBytes;
    }

    public sealed record LoadResult(string Scenario, int Operations, int Concurrency, double WallSeconds,
        double P50Ms, double P95Ms, long AllocatedBytes, int Gen0, int Gen1, int Gen2,
        double CpuMs, int MaxThreadPoolThreads, long MaxThreadPoolQueue, long MaxHeapBytes, long MaxLohBytes);
}