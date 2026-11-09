using BankFlow.Application.Interfaces;
using BankFlow.Application.Services;
using BankFlow.Domain;
using BankFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BANKFLOW_TEST_SQL")))
            Skip = "Set BANKFLOW_TEST_SQL to a local SQL Server test connection with database creation permissions.";
    }
}

public sealed class SqlTransferTests
{
    [SqlServerFact]
    public async Task DuplicateRequests_ShouldReturnOneReceiptAndOneReplay()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var gate = new CommitGate();
        var key = Guid.NewGuid();
        var results = await Task.WhenAll(Execute(database, gate, key), Execute(database, gate, key));
        Assert.All(results, result => Assert.Null(result.Conflict));
        Assert.Single(results.Where(result => !result.Outcome!.Replayed));
        Assert.Single(results.Where(result => result.Outcome!.Replayed));
        Assert.Equal(results[0].Outcome!.Transfer.Id, results[1].Outcome!.Transfer.Id);
        await VerifySingleCommit(database);
    }

    [SqlServerFact]
    public async Task CompetingTransfers_ShouldNotOverspendOrLoseMoney()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        var gate = new CommitGate();
        var results = await Task.WhenAll(Execute(database, gate, Guid.NewGuid()), Execute(database, gate, Guid.NewGuid()));
        Assert.Single(results.Where(result => result.Outcome is not null));
        Assert.Equal("balance_changed", results.Single(result => result.Conflict is not null).Conflict);
        await VerifySingleCommit(database);
    }

    [SqlServerFact]
    public async Task FailedCredit_ShouldRollbackTheEntireSqlTransaction()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await using (var setup = database.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailCredit ON dbo.Transactions AFTER INSERT AS
                BEGIN
                    IF EXISTS (SELECT 1 FROM inserted WHERE [Type] = 'TransferIn')
                        THROW 50010, 'Injected transfer credit failure.', 1;
                END
                """);
        }
        await using (var db = database.CreateContext())
        {
            var service = new TransferService(new SqlTransferUnitOfWork(db));
            await Assert.ThrowsAsync<DbUpdateException>(() => service.ExecuteAsync(database.SourceId,
                "DEST0001", 70m, "", "test-client", false, Guid.NewGuid()));
        }
        await using var verification = database.CreateContext();
        Assert.Equal(100m, (await verification.Accounts.FindAsync(database.SourceId))!.Balance);
        Assert.Equal(0m, (await verification.Accounts.FindAsync(database.DestinationId))!.Balance);
        Assert.Equal(0, await verification.Transfers.CountAsync());
        Assert.Equal(0, await verification.Transactions.CountAsync());
    }

    private static async Task<(TransferOutcome? Outcome, string? Conflict)> Execute(TestDatabase database, CommitGate gate, Guid key)
    {
        await using var db = database.CreateContext();
        var service = new TransferService(new CoordinatedUnitOfWork(new SqlTransferUnitOfWork(db), gate));
        try { return (await service.ExecuteAsync(database.SourceId, "DEST0001", 70m, "", "test-client", false, key), null); }
        catch (TransferConflictException exception) { return (null, exception.Code); }
    }

    private static async Task VerifySingleCommit(TestDatabase database)
    {
        await using var db = database.CreateContext();
        Assert.Equal(30m, (await db.Accounts.FindAsync(database.SourceId))!.Balance);
        Assert.Equal(70m, (await db.Accounts.FindAsync(database.DestinationId))!.Balance);
        Assert.Equal(100m, await db.Accounts.SumAsync(account => account.Balance));
        Assert.Equal(1, await db.Transfers.CountAsync());
        Assert.Equal(2, await db.Transactions.CountAsync());
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("BANKFLOW_TEST_SQL"))
        {
            InitialCatalog = "BankFlow_TransferTests_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;

        public Guid SourceId { get; } = Guid.NewGuid();
        public Guid DestinationId { get; } = Guid.NewGuid();
        public BankFlowDbContext CreateContext() => new(new DbContextOptionsBuilder<BankFlowDbContext>()
            .UseSqlServer(_connection, options => options.CommandTimeout(20)).Options);

        public async Task InitializeAsync()
        {
            await using var db = CreateContext();
            await db.Database.MigrateAsync();
            var source = new Account(SourceId, "Test source", "SOURCE01", "test-client");
            source.Deposit(100m);
            db.Accounts.AddRange(source, new Account(DestinationId, "Test destination", "DEST0001", "another-client"));
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = CreateContext();
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class CommitGate
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
            return _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private sealed class CoordinatedUnitOfWork(ITransferUnitOfWork inner, CommitGate gate) : ITransferUnitOfWork
    {
        public Task<Account?> GetAccountAsync(Guid id, CancellationToken cancellationToken) => inner.GetAccountAsync(id, cancellationToken);
        public Task<Account?> GetAccountByNumberAsync(string number, CancellationToken cancellationToken) => inner.GetAccountByNumberAsync(number, cancellationToken);
        public Task<Transfer?> GetByKeyAsync(string actor, Guid key, CancellationToken cancellationToken) => inner.GetByKeyAsync(actor, key, cancellationToken);
        public Task<Transfer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);
        public void Add(Transfer transfer, Transaction debit, Transaction credit) => inner.Add(transfer, debit, credit);
        public async Task<TransferOutcome> CommitAsync(Transfer transfer, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return await inner.CommitAsync(transfer, cancellationToken);
        }
    }
}