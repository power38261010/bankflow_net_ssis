using BankFlow.Application.Interfaces;
using BankFlow.Domain;

namespace BankFlow.Infrastructure.Repositories;

public sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly Dictionary<Guid, Account> _accounts = new();

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_accounts.TryGetValue(id, out var account) ? account : null);

    public Task<Account?> GetByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken = default)
        => Task.FromResult(_accounts.Values.FirstOrDefault(a => a.AccountNumber == accountNumber));

    public Task<IEnumerable<Account>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Account>>(_accounts.Values.ToList());

    public Task<IEnumerable<Account>> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Account>>(_accounts.Values.Where(account => account.OwnerUserId == ownerUserId).ToList());

    public Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
