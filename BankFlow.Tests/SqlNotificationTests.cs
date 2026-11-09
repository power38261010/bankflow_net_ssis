using BankFlow.Application.Interfaces;
using BankFlow.Application.Services;
using BankFlow.Domain;
using BankFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BankFlow.Tests;

public sealed class SqlNotificationTests
{
    [SqlServerFact]
    public async Task Transfer_ShouldAtomicallyCreatePrivateInboxNotificationsAndPushRealtime()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        await using var db = fixture.Context();
        var source = await db.Accounts.SingleAsync(account => account.Id == fixture.SourceId);
        var destination = await db.Accounts.SingleAsync(account => account.Id == fixture.DestinationId);
        var transfer = new Transfer(source, destination, "source-owner", Guid.NewGuid(), 25m, "monthly rent");
        var unit = new SqlTransferUnitOfWork(db);
        unit.Add(transfer, new Transaction(Guid.NewGuid(), source.Id, 25m, "TransferOut", "Sent", transfer.Id),
            new Transaction(Guid.NewGuid(), destination.Id, 25m, "TransferIn", "Received", transfer.Id));
        await unit.CommitAsync(transfer, default);
        Assert.Equal(2, await db.OutboxMessages.CountAsync()); Assert.Equal(0, await db.Notifications.CountAsync());

        var sink = new RecordingSink();
        var publisher = fixture.Publisher(db, sink);
        Assert.Equal(2, await publisher.ProcessBatchAsync(10, default));
        Assert.Equal(2, await db.Notifications.CountAsync());
        Assert.Equal(2, sink.Events.Count);
        Assert.Contains(sink.Events, item => item.Recipient == "source-owner");
        Assert.Contains(sink.Events, item => item.Recipient == "destination-owner");
        Assert.DoesNotContain(sink.Events, item => item.Recipient == "another-owner");
        Assert.All(await db.OutboxMessages.ToListAsync(), message => Assert.NotNull(message.RealtimeSentAt));
        Assert.Equal(0, await publisher.ProcessBatchAsync(10, default));
        Assert.Equal(2, await db.Notifications.CountAsync());

        var store = new SqlNotificationStore(db, TimeProvider.System);
        var privatePage = await store.PageAsync("source-owner", 1, 20, default);
        Assert.Single(privatePage.Items); Assert.Equal(1, privatePage.UnreadCount);
        Assert.Empty((await store.PageAsync("another-owner", 1, 20, default)).Items);
        Assert.True(await store.MarkReadAsync("source-owner", privatePage.Items[0].Id, default));
        Assert.False(await store.MarkReadAsync("another-owner", privatePage.Items[0].Id, default));
        Assert.Equal(0, (await store.PageAsync("source-owner", 1, 20, default)).UnreadCount);
    }

    [SqlServerFact]
    public async Task Outbox_ShouldRetryHubAfterFailureWithoutDuplicatingInbox()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        await using var db = fixture.Context();
        var source = await db.Accounts.SingleAsync(account => account.Id == fixture.SourceId);
        var destination = await db.Accounts.SingleAsync(account => account.Id == fixture.DestinationId);
        var transfer = new Transfer(source, destination, "source-owner", Guid.NewGuid(), 25m, "");
        var unit = new SqlTransferUnitOfWork(db);
        unit.Add(transfer, new Transaction(Guid.NewGuid(), source.Id, 25m, "TransferOut", "Sent", transfer.Id),
            new Transaction(Guid.NewGuid(), destination.Id, 25m, "TransferIn", "Received", transfer.Id));
        await unit.CommitAsync(transfer, default);
        var sink = new RecordingSink { FailFirst = true };
        var publisher = fixture.Publisher(db, sink);
        Assert.Equal(2, await publisher.ProcessBatchAsync(10, default));
        Assert.Equal(2, await db.Notifications.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync(message => message.RealtimeSentAt == null));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE OutboxMessages SET LeaseUntil={DateTimeOffset.UtcNow.AddSeconds(-1).UtcTicks} WHERE RealtimeSentAt IS NULL;");
        Assert.Equal(1, await publisher.ProcessBatchAsync(10, default));
        Assert.Equal(2, await db.Notifications.CountAsync());
        Assert.Equal(2, sink.Delivered.Count);
    }

    [SqlServerFact]
    public async Task FailedOutboxInsert_ShouldRollbackTransferBalancesAndMovements()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        await using (var setup = fixture.Context())
            await setup.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailOutboxInsert ON dbo.OutboxMessages AFTER INSERT AS BEGIN THROW 50011, 'Injected outbox persistence failure.', 1; END;");
        await using (var db = fixture.Context())
        {
            var source = await db.Accounts.SingleAsync(account => account.Id == fixture.SourceId);
            var destination = await db.Accounts.SingleAsync(account => account.Id == fixture.DestinationId);
            var transfer = new Transfer(source, destination, "source-owner", Guid.NewGuid(), 25m, "");
            var unit = new SqlTransferUnitOfWork(db);
            unit.Add(transfer, new Transaction(Guid.NewGuid(), source.Id, 25m, "TransferOut", "Sent", transfer.Id),
                new Transaction(Guid.NewGuid(), destination.Id, 25m, "TransferIn", "Received", transfer.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => unit.CommitAsync(transfer, default));
        }
        await using var verification = fixture.Context();
        Assert.Equal(100m, (await verification.Accounts.FindAsync(fixture.SourceId))!.Balance);
        Assert.Equal(0m, (await verification.Accounts.FindAsync(fixture.DestinationId))!.Balance);
        Assert.Equal(0, await verification.Transfers.CountAsync());
        Assert.Equal(0, await verification.Transactions.CountAsync());
        Assert.Equal(0, await verification.OutboxMessages.CountAsync());
    }

    private sealed class RecordingSink : INotificationRealtimeSink
    {
        public List<(string Recipient, Guid Id)> Events { get; } = [];
        public List<Guid> Delivered { get; } = [];
        public bool FailFirst { get; set; }
        public Task NotifyAvailableAsync(string recipientUserId, Guid notificationId, CancellationToken cancellationToken)
        {
            Events.Add((recipientUserId, notificationId));
            if (FailFirst) { FailFirst = false; throw new InvalidOperationException("Injected SignalR failure."); }
            Delivered.Add(notificationId); return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("BANKFLOW_TEST_SQL"))
        { InitialCatalog = "BankFlow_NotificationTests_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        public Guid SourceId { get; } = Guid.NewGuid();
        public Guid DestinationId { get; } = Guid.NewGuid();
        public BankFlowDbContext Context() => new(new DbContextOptionsBuilder<BankFlowDbContext>().UseSqlServer(_connection).Options);
        public SqlOutboxPublisher Publisher(BankFlowDbContext db, INotificationRealtimeSink sink) =>
            new(db, TimeProvider.System, sink);
        public async Task InitializeAsync()
        {
            await using var db = Context(); await db.Database.MigrateAsync();
            var source = new Account(SourceId, "Sender", "NOTIFY001", "source-owner"); source.Deposit(100m);
            db.Accounts.AddRange(source, new Account(DestinationId, "Receiver", "NOTIFY002", "destination-owner"),
                new Account(Guid.NewGuid(), "Private customer", "NOTIFY003", "another-owner"));
            await db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync() { await using var db = Context(); await db.Database.EnsureDeletedAsync(); }
    }
}