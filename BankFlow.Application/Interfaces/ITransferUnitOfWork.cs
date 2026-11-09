using BankFlow.Domain;

namespace BankFlow.Application.Interfaces;

public sealed record TransferOutcome(Transfer Transfer, bool Replayed);

public interface ITransferUnitOfWork
{
    Task<Account?> GetAccountAsync(Guid id, CancellationToken cancellationToken);
    Task<Account?> GetAccountByNumberAsync(string number, CancellationToken cancellationToken);
    Task<Transfer?> GetByKeyAsync(string actorUserId, Guid key, CancellationToken cancellationToken);
    Task<Transfer?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(Transfer transfer, Transaction debit, Transaction credit);
    Task<TransferOutcome> CommitAsync(Transfer transfer, CancellationToken cancellationToken);
}