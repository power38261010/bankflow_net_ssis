using System.Data.Common;
using BankFlow.Domain;
using BankFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BankFlow.Tests;

public sealed class SqlReadQueriesTests
{
    [SqlServerFact]
    public async Task Pages_ShouldUseFixedQueryCountsProjectInSqlAndKeepSummaryGlobal()
    {
        var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("BANKFLOW_TEST_SQL"))
        {
            InitialCatalog = "BankFlow_ReadTests_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        var capture = new QueryCapture();
        var options = new DbContextOptionsBuilder<BankFlowDbContext>().UseSqlServer(connection).AddInterceptors(capture).Options;
        await using var db = new BankFlowDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            var source = new Account(Guid.NewGuid(), "Read owner", "READ0001", "owner");
            source.Deposit(25m);
            db.Accounts.Add(source);
            for (var index = 2; index <= 25; index++)
            {
                var account = new Account(Guid.NewGuid(), "Read owner", $"READ{index:D4}", "owner");
                account.Deposit(1m); db.Accounts.Add(account);
            }
            db.Accounts.Add(new Account(Guid.NewGuid(), "Other owner", "FOREIGN001", "other"));
            for (var index = 0; index < 35; index++)
                db.Transactions.Add(new Transaction(Guid.NewGuid(), source.Id, 1m, "Deposit", "Synthetic test"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var queries = new SqlBankingReadQueries(db);
            capture.Commands.Clear();
            var first = await queries.AccountsAsync(1, 10, null, "owner");
            Assert.Equal(25, first.TotalCount); Assert.Equal(10, first.Items.Count);
            Assert.Equal(2, capture.Commands.Count);
            Assert.Contains("OFFSET", capture.Commands[1]); Assert.Contains("FETCH NEXT", capture.Commands[1]);
            Assert.All(capture.Commands, sql => Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(db.ChangeTracker.Entries());
            capture.Commands.Clear();
            var larger = await queries.AccountsAsync(1, 25, null, "owner");
            Assert.Equal(25, larger.Items.Count); Assert.Equal(2, capture.Commands.Count);
            capture.Commands.Clear();
            var summary = await queries.SummaryAsync("owner");
            Assert.Equal(25, summary.AccountCount); Assert.Equal(49m, summary.TotalBalance);
            Assert.Single(capture.Commands); Assert.Contains("SUM(", capture.Commands[0]);
            capture.Commands.Clear();
            var movements = await queries.MovementsAsync(source.Id, 2, 10, "owner");
            Assert.Equal(35, movements!.TotalCount); Assert.Equal(10, movements.Items.Count);
            Assert.Equal(3, capture.Commands.Count);
            capture.Commands.Clear();
            Assert.Null(await queries.MovementsAsync(source.Id, 1, 10, "other"));
            Assert.Single(capture.Commands);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private sealed class QueryCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("BankFlow.", StringComparison.Ordinal)) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}