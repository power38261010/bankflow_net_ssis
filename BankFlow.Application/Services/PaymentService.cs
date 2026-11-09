using BankFlow.Application.Interfaces;
using BankFlow.Domain;

namespace BankFlow.Application.Services;

public sealed class PaymentService(IPaymentRepository repository, TimeProvider clock)
{
    public async Task<Beneficiary> CreateBeneficiaryAsync(string actor, string name, string number, CancellationToken cancellationToken)
    {
        var destination = await repository.DestinationAsync(number.Trim(), cancellationToken);
        if (destination?.OwnerUserId is null) throw new KeyNotFoundException("Destination account is not available.");
        var beneficiary = new Beneficiary(actor, name, destination.AccountNumber, clock.GetUtcNow());
        repository.Add(beneficiary); await repository.SaveAsync(cancellationToken); return beneficiary;
    }

    public async Task ArchiveBeneficiaryAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        var beneficiary = await repository.BeneficiaryAsync(id, actor, cancellationToken)
            ?? throw new KeyNotFoundException("Beneficiary is not available.");
        beneficiary.Archive(); await repository.SaveAsync(cancellationToken);
    }

    public async Task<ScheduledPayment> CreatePaymentAsync(string actor, bool isOperator, Guid sourceId, Guid beneficiaryId,
        decimal amount, string? description, PaymentFrequency frequency, DateTimeOffset dueAt, Guid creationKey, CancellationToken cancellationToken)
    {
        var source = await repository.AccountAsync(sourceId, cancellationToken);
        if (source?.OwnerUserId is null || !isOperator && source.OwnerUserId != actor)
            throw new KeyNotFoundException("Source account is not available.");
        if (creationKey == Guid.Empty) throw new ArgumentException("Creation key is required.");
        var existing = await repository.PaymentByCreationAsync(actor, creationKey, cancellationToken);
        if (existing is not null)
        {
            if (!existing.Matches(sourceId, beneficiaryId, amount, description, frequency, dueAt)) throw new InvalidOperationException("Creation key already used with different data.");
            return existing;
        }
        var beneficiary = await repository.BeneficiaryAsync(beneficiaryId, actor, cancellationToken)
            ?? throw new KeyNotFoundException("Beneficiary is not available.");
        var destination = await repository.DestinationAsync(beneficiary.AccountNumber, cancellationToken);
        if (destination?.OwnerUserId is null) throw new KeyNotFoundException("Destination account is not available.");
        if (source.Id == destination.Id) throw new ArgumentException("Select a different destination.");
        var payment = new ScheduledPayment(source.OwnerUserId, actor, source.Id, beneficiary, amount, description,
            frequency, dueAt, clock.GetUtcNow(), creationKey);
        repository.Add(payment);
        var committed = await repository.CommitPaymentAsync(payment, cancellationToken);
        if (!committed.Matches(sourceId, beneficiaryId, amount, description, frequency, dueAt)) throw new InvalidOperationException("Creation key already used with different data.");
        return committed;
    }

    public async Task CancelPaymentAsync(Guid id, string actor, bool isOperator, CancellationToken cancellationToken)
    {
        var payment = await repository.PaymentAsync(id, cancellationToken);
        if (payment is null || !isOperator && payment.OwnerUserId != actor) throw new KeyNotFoundException("Payment is not available.");
        payment.Cancel(clock.GetUtcNow()); await repository.SaveAsync(cancellationToken);
    }
}