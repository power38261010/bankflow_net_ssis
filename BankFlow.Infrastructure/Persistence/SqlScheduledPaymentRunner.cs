using BankFlow.Application.Interfaces;
using BankFlow.Application.Services;
using BankFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Infrastructure.Persistence;

public sealed class SqlScheduledPaymentRunner(BankFlowDbContext db, TimeProvider clock,
    IPaymentExecutionAuthorizer authorizer) : IScheduledPaymentRunner
{
    public async Task<PaymentClaim?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var candidates = await db.ScheduledPayments.Where(payment => payment.NextRunAt <= now && payment.NextAttemptAt <= now &&
                (payment.Status == PaymentStatus.Pending || payment.Status == PaymentStatus.Processing && payment.LeaseUntil <= now))
            .OrderBy(payment => payment.NextAttemptAt).ThenBy(payment => payment.Id).Take(10).ToListAsync(cancellationToken);
        foreach (var payment in candidates)
        {
            if (!payment.TryClaim(now, TimeSpan.FromMinutes(2))) continue;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new PaymentClaim(payment.Id, payment.LeaseToken!.Value);
            }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return null; }
        }
        return null;
    }

    public async Task ExecuteAsync(PaymentClaim claim, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var payment = await db.ScheduledPayments.SingleOrDefaultAsync(candidate => candidate.Id == claim.PaymentId, cancellationToken);
        if (payment is null) return;
        try
        {
            payment.RequireLease(claim.LeaseToken, clock.GetUtcNow());
            if (!await authorizer.CanExecuteAsync(payment.CreatedByUserId, payment.OwnerUserId, cancellationToken))
            {
                payment.Fail(claim.LeaseToken, "authorization_revoked", clock.GetUtcNow(), false);
                await db.SaveChangesAsync(cancellationToken); return;
            }
            var store = new ScheduledTransferStore(new SqlTransferUnitOfWork(db), payment, claim.LeaseToken, clock);
            var service = new TransferService(store);
            var outcome = await service.ExecuteAsync(payment.SourceAccountId, payment.DestinationAccountNumber, payment.Amount,
                payment.Description, payment.OwnerUserId, false, payment.OccurrenceKey, cancellationToken);
            if (outcome.Replayed)
            {
                db.ChangeTracker.Clear();
                var current = await db.ScheduledPayments.SingleAsync(candidate => candidate.Id == claim.PaymentId, cancellationToken);
                if (current.LastTransferId == outcome.Transfer.Id) return;
                current.Complete(claim.LeaseToken, outcome.Transfer.Id, clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            var current = await db.ScheduledPayments.SingleAsync(candidate => candidate.Id == claim.PaymentId, cancellationToken);
            if (current.Status != PaymentStatus.Processing || current.LeaseToken != claim.LeaseToken || current.LeaseUntil <= clock.GetUtcNow()) return;
            var code = exception switch
            {
                TransferConflictException conflict => conflict.Code,
                KeyNotFoundException => "account_unavailable",
                ArgumentException => "invalid_payment",
                _ => "temporary_failure"
            };
            var retry = code is "balance_changed" or "temporary_failure";
            current.Fail(claim.LeaseToken, code, clock.GetUtcNow(), retry);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
        }
    }

    private sealed class ScheduledTransferStore(ITransferUnitOfWork inner, ScheduledPayment payment,
        Guid lease, TimeProvider clock) : ITransferUnitOfWork
    {
        public Task<Account?> GetAccountAsync(Guid id, CancellationToken cancellationToken) => inner.GetAccountAsync(id, cancellationToken);
        public Task<Account?> GetAccountByNumberAsync(string number, CancellationToken cancellationToken) => inner.GetAccountByNumberAsync(number, cancellationToken);
        public Task<Transfer?> GetByKeyAsync(string actor, Guid key, CancellationToken cancellationToken) => inner.GetByKeyAsync(actor, key, cancellationToken);
        public Task<Transfer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);
        public void Add(Transfer transfer, Transaction debit, Transaction credit) => inner.Add(transfer, debit, credit);
        public Task<TransferOutcome> CommitAsync(Transfer transfer, CancellationToken cancellationToken)
        {
            payment.Complete(lease, transfer.Id, clock.GetUtcNow());
            return inner.CommitAsync(transfer, cancellationToken);
        }
    }
}