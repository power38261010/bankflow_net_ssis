using BankFlow.Application.Interfaces;
using BankFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Infrastructure.Persistence;

public sealed class SqlPaymentRepository(BankFlowDbContext db, TimeProvider clock) : IPaymentRepository
{
    public Task<Account?> AccountAsync(Guid id, CancellationToken cancellationToken) => db.Accounts.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
    public Task<Account?> DestinationAsync(string number, CancellationToken cancellationToken) => db.Accounts.SingleOrDefaultAsync(account => account.AccountNumber == number, cancellationToken);
    public Task<Beneficiary?> BeneficiaryAsync(Guid id, string owner, CancellationToken cancellationToken) =>
        db.Beneficiaries.SingleOrDefaultAsync(beneficiary => beneficiary.Id == id && beneficiary.OwnerUserId == owner && beneficiary.Active, cancellationToken);
    public Task<ScheduledPayment?> PaymentAsync(Guid id, CancellationToken cancellationToken) => db.ScheduledPayments.SingleOrDefaultAsync(payment => payment.Id == id, cancellationToken);
    public Task<ScheduledPayment?> PaymentByCreationAsync(string creator, Guid key, CancellationToken cancellationToken) =>
        db.ScheduledPayments.AsNoTracking().SingleOrDefaultAsync(payment => payment.CreatedByUserId == creator && payment.CreationKey == key, cancellationToken);
    public async Task<ScheduledPayment> CommitPaymentAsync(ScheduledPayment payment, CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); return payment; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var existing = await PaymentByCreationAsync(payment.CreatedByUserId, payment.CreationKey, cancellationToken);
            if (existing is not null) return existing;
            throw;
        }
    }
    public void Add(Beneficiary beneficiary) => db.Beneficiaries.Add(beneficiary);
    public void Add(ScheduledPayment payment) => db.ScheduledPayments.Add(payment);
    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public async Task<PageResult<BeneficiaryItem>> BeneficiariesAsync(string owner, int page, int pageSize, CancellationToken cancellationToken)
    {
        ValidatePage(page, pageSize);
        var query = db.Beneficiaries.AsNoTracking().Where(beneficiary => beneficiary.OwnerUserId == owner && beneficiary.Active);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(beneficiary => beneficiary.Name).ThenBy(beneficiary => beneficiary.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(beneficiary => new BeneficiaryItem(
                beneficiary.Id, beneficiary.Name, beneficiary.AccountNumber, beneficiary.CreatedAt)).ToListAsync(cancellationToken);
        return new PageResult<BeneficiaryItem>(items, page, pageSize, total);
    }

    public async Task<PageResult<ScheduledPaymentItem>> PaymentsAsync(string? owner, int page, int pageSize, CancellationToken cancellationToken)
    {
        ValidatePage(page, pageSize);
        var query = db.ScheduledPayments.AsNoTracking();
        if (owner is not null) query = query.Where(payment => payment.OwnerUserId == owner);
        var total = await query.CountAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var items = await query.Join(db.Accounts, payment => payment.SourceAccountId, account => account.Id,
                (payment, account) => new { Payment = payment, SourceNumber = account.AccountNumber })
            .OrderByDescending(item => item.Payment.CreatedAt).ThenByDescending(item => item.Payment.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new ScheduledPaymentItem(item.Payment.Id, item.SourceNumber, item.Payment.BeneficiaryName,
                item.Payment.DestinationAccountNumber, item.Payment.Amount, item.Payment.Description, item.Payment.Frequency,
                item.Payment.Status, item.Payment.NextRunAt, item.Payment.LastRunAt, item.Payment.LastTransferId,
                item.Payment.FailureCode, item.Payment.Status == PaymentStatus.Pending ||
                    item.Payment.Status == PaymentStatus.Processing && item.Payment.LeaseUntil <= now)).ToListAsync(cancellationToken);
        return new PageResult<ScheduledPaymentItem>(items, page, pageSize, total);
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page.");
    }
}