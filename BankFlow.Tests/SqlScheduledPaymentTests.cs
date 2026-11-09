using BankFlow.Application.Interfaces;
using BankFlow.Domain;
using BankFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BankFlow.Tests;

public sealed class SqlScheduledPaymentTests
{
    [SqlServerFact]
    public async Task Payment_ShouldAllowOnlyOneConcurrentClaim()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        var gate = new ClaimGate();
        await using var firstDb = fixture.Context(gate);
        await using var secondDb = fixture.Context(gate);
        var claims = await Task.WhenAll(fixture.Runner(firstDb).ClaimNextAsync(default), fixture.Runner(secondDb).ClaimNextAsync(default));
        var claim = Assert.Single(claims.Where(candidate => candidate is not null));
        await using var execution = fixture.Context();
        await fixture.Runner(execution).ExecuteAsync(claim!, default);
        await using var check = fixture.Context();
        Assert.Equal(1, await check.Transfers.CountAsync());
        Assert.Equal(PaymentStatus.Completed, (await check.ScheduledPayments.SingleAsync()).Status);
    }

    private sealed class ClaimGate : SaveChangesInterceptor
    {
        private int _arrivals;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ScheduledPayment>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Status == PaymentStatus.Processing))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
    [SqlServerFact]
    public async Task Payment_ShouldCommitMoneyAndStateOnceAndRecoverExpiredLease()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        await using var firstDb = fixture.Context();
        var first = fixture.Runner(firstDb);
        var claim = (await first.ClaimNextAsync(default))!;
        await using (var competingDb = fixture.Context()) Assert.Null(await fixture.Runner(competingDb).ClaimNextAsync(default));
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        await using var recoveredDb = fixture.Context();
        var recovered = fixture.Runner(recoveredDb);
        var replacement = (await recovered.ClaimNextAsync(default))!;
        Assert.NotEqual(claim.LeaseToken, replacement.LeaseToken);
        await first.ExecuteAsync(claim, default);
        await recovered.ExecuteAsync(replacement, default);
        await recovered.ExecuteAsync(replacement, default);
        await using var check = fixture.Context();
        var payment = await check.ScheduledPayments.SingleAsync();
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(75m, (await check.Accounts.FindAsync(fixture.Source))!.Balance);
        Assert.Equal(25m, (await check.Accounts.FindAsync(fixture.Destination))!.Balance);
        Assert.Equal(1, await check.Transfers.CountAsync()); Assert.Equal(2, await check.Transactions.CountAsync());
        Assert.Equal(payment.LastTransferId, (await check.Transfers.SingleAsync()).Id);
    }

    [SqlServerFact]
    public async Task Payment_ShouldRollbackCreditFailureAndRetrySameOccurrence()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        await using (var trigger = fixture.Context())
        {
            await trigger.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailScheduledCredit ON dbo.Transactions AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE [Type]='TransferIn') THROW 50010, 'Injected failure.', 1; END;");
        }
        await using (var execution = fixture.Context())
        {
            var runner = fixture.Runner(execution); var claim = (await runner.ClaimNextAsync(default))!;
            await runner.ExecuteAsync(claim, default);
        }
        await using (var check = fixture.Context())
        {
            Assert.Equal(100m, (await check.Accounts.FindAsync(fixture.Source))!.Balance);
            Assert.Equal(0, await check.Transfers.CountAsync()); Assert.Equal(0, await check.Transactions.CountAsync());
            var payment = await check.ScheduledPayments.SingleAsync();
            Assert.Equal(PaymentStatus.Pending, payment.Status); Assert.Equal(fixture.Key, payment.OccurrenceKey);
            await check.Database.ExecuteSqlRawAsync("DROP TRIGGER FailScheduledCredit;");
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await using (var retry = fixture.Context())
        {
            var runner = fixture.Runner(retry); await runner.ExecuteAsync((await runner.ClaimNextAsync(default))!, default);
        }
        await using var final = fixture.Context();
        Assert.Equal(PaymentStatus.Completed, (await final.ScheduledPayments.SingleAsync()).Status);
        Assert.Equal(fixture.Key, (await final.Transfers.SingleAsync()).IdempotencyKey);
        Assert.Equal(75m, (await final.Accounts.FindAsync(fixture.Source))!.Balance);
    }

    [SqlServerFact]
    public async Task Payment_ShouldRejectRevokedAuthorizationWithoutMovingMoney()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        await using (var db = fixture.Context())
        {
            var runner = fixture.Runner(db, false); await runner.ExecuteAsync((await runner.ClaimNextAsync(default))!, default);
        }
        await using var check = fixture.Context();
        var payment = await check.ScheduledPayments.SingleAsync();
        Assert.Equal(PaymentStatus.Failed, payment.Status); Assert.Equal("authorization_revoked", payment.FailureCode);
        Assert.Equal(100m, (await check.Accounts.FindAsync(fixture.Source))!.Balance);
        Assert.Equal(0, await check.Transfers.CountAsync());
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
    private sealed class Authorizer(bool allowed) : IPaymentExecutionAuthorizer
    {
        public Task<bool> CanExecuteAsync(string creator, string owner, CancellationToken cancellationToken) => Task.FromResult(allowed);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("BANKFLOW_TEST_SQL"))
        { InitialCatalog = "BankFlow_ScheduledTests_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        public Clock Clock { get; } = new();
        public Guid Source { get; } = Guid.NewGuid();
        public Guid Destination { get; } = Guid.NewGuid();
        public Guid Key { get; private set; }
        public BankFlowDbContext Context(SaveChangesInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<BankFlowDbContext>().UseSqlServer(_connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new BankFlowDbContext(options.Options);
        }
        public SqlScheduledPaymentRunner Runner(BankFlowDbContext db, bool allowed = true) => new(db, Clock, new Authorizer(allowed));
        public async Task InitializeAsync()
        {
            await using var db = Context(); await db.Database.MigrateAsync();
            var source = new Account(Source, "Sender", "SCHEDULE001", "owner"); source.Deposit(100m);
            var beneficiary = new Beneficiary("owner", "Receiver", "SCHEDULE002", Clock.GetUtcNow());
            var payment = new ScheduledPayment("owner", "owner", source.Id, beneficiary, 25m, "Scheduled test", PaymentFrequency.Once,
                Clock.GetUtcNow().AddMinutes(1), Clock.GetUtcNow());
            Key = payment.OccurrenceKey;
            db.Accounts.AddRange(source, new Account(Destination, "Receiver", "SCHEDULE002", "receiver"));
            db.Beneficiaries.Add(beneficiary); db.ScheduledPayments.Add(payment); await db.SaveChangesAsync();
            Clock.Advance(TimeSpan.FromMinutes(1));
        }
        public async ValueTask DisposeAsync() { await using var db = Context(); await db.Database.EnsureDeletedAsync(); }
    }
}