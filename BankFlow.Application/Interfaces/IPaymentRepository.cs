using BankFlow.Domain;

namespace BankFlow.Application.Interfaces;

public sealed record BeneficiaryItem(Guid Id, string Name, string AccountNumber, DateTimeOffset CreatedAt);
public sealed record ScheduledPaymentItem(Guid Id, string SourceAccountNumber, string BeneficiaryName,
    string DestinationAccountNumber, decimal Amount, string Description, PaymentFrequency Frequency, PaymentStatus Status,
    DateTimeOffset NextRunAt, DateTimeOffset? LastRunAt, Guid? LastTransferId, string? FailureCode, bool CanCancel);

public interface IPaymentRepository
{
    Task<Account?> AccountAsync(Guid id, CancellationToken cancellationToken);
    Task<Account?> DestinationAsync(string number, CancellationToken cancellationToken);
    Task<Beneficiary?> BeneficiaryAsync(Guid id, string owner, CancellationToken cancellationToken);
    Task<PageResult<BeneficiaryItem>> BeneficiariesAsync(string owner, int page, int pageSize, CancellationToken cancellationToken);
    Task<ScheduledPayment?> PaymentAsync(Guid id, CancellationToken cancellationToken);
    Task<ScheduledPayment?> PaymentByCreationAsync(string creator, Guid key, CancellationToken cancellationToken);
    Task<ScheduledPayment> CommitPaymentAsync(ScheduledPayment payment, CancellationToken cancellationToken);
    Task<PageResult<ScheduledPaymentItem>> PaymentsAsync(string? owner, int page, int pageSize, CancellationToken cancellationToken);
    void Add(Beneficiary beneficiary);
    void Add(ScheduledPayment payment);
    Task SaveAsync(CancellationToken cancellationToken);
}