using BankFlow.Application.Interfaces;
using BankFlow.Application.Services;
using BankFlow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace BankFlow.Infrastructure.Persistence;

public sealed class SqlTransferUnitOfWork(BankFlowDbContext db) : ITransferUnitOfWork
{
    public Task<Account?> GetAccountAsync(Guid id, CancellationToken cancellationToken) =>
        db.Accounts.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);

    public Task<Account?> GetAccountByNumberAsync(string number, CancellationToken cancellationToken) =>
        db.Accounts.SingleOrDefaultAsync(account => account.AccountNumber == number, cancellationToken);

    public Task<Transfer?> GetByKeyAsync(string actorUserId, Guid key, CancellationToken cancellationToken) =>
        db.Transfers.AsNoTracking().SingleOrDefaultAsync(transfer => transfer.ActorUserId == actorUserId &&
            transfer.IdempotencyKey == key, cancellationToken);

    public Task<Transfer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Transfers.AsNoTracking().SingleOrDefaultAsync(transfer => transfer.Id == id, cancellationToken);

    public void Add(Transfer transfer, Transaction debit, Transaction credit)
    {
        db.Transfers.Add(transfer);
        db.Transactions.AddRange(debit, credit);
        var source = db.Accounts.Local.Single(account => account.Id == transfer.SourceAccountId);
        var destination = db.Accounts.Local.Single(account => account.Id == transfer.DestinationAccountId);
        var sourceOwner = source.OwnerUserId!;
        var destinationOwner = destination.OwnerUserId!;
        var sourcePayload = JsonSerializer.Serialize(new { id = transfer.Id, amount = transfer.Amount, counterpartyLastFour = LastFour(destination.AccountNumber), createdAt = transfer.CreatedAt });
        var destinationPayload = JsonSerializer.Serialize(new { id = transfer.Id, amount = transfer.Amount, counterpartyLastFour = LastFour(source.AccountNumber), createdAt = transfer.CreatedAt });
        db.OutboxMessages.Add(new OutboxMessage(sourceOwner, "TransferSent", sourcePayload, transfer.Id, transfer.CreatedAt));
        if (destinationOwner != sourceOwner)
            db.OutboxMessages.Add(new OutboxMessage(destinationOwner, "TransferReceived", destinationPayload, transfer.Id, transfer.CreatedAt));
    }

    private static string LastFour(string accountNumber) => accountNumber.Length <= 4 ? accountNumber : accountNumber[^4..];

    public async Task<TransferOutcome> CommitAsync(Transfer transfer, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new TransferOutcome(transfer, false);
        }
        catch (DbUpdateException exception)
        {
            db.ChangeTracker.Clear();
            var existing = await GetByKeyAsync(transfer.ActorUserId, transfer.IdempotencyKey, cancellationToken);
            if (existing is not null) return new TransferOutcome(existing, true);
            if (exception is DbUpdateConcurrencyException) throw new TransferConflictException("balance_changed");
            if (exception.InnerException is SqlException { Number: 1205 }) throw new TransferConflictException("balance_changed");
            throw;
        }
    }
}