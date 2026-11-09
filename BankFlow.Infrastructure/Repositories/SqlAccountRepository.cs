using BankFlow.Application.Interfaces;
using BankFlow.Domain;
using BankFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Infrastructure.Repositories;

public sealed class SqlAccountRepository : IAccountRepository
{
    private readonly BankFlowDbContext _context;

    public SqlAccountRepository(BankFlowDbContext context)
    {
        _context = context;
    }

    public async Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await _context.Accounts.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<Account?> GetByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken = default)
        => await _context.Accounts.FirstOrDefaultAsync(x => x.AccountNumber == accountNumber, cancellationToken);

    public async Task<IEnumerable<Account>> GetAllAsync(CancellationToken cancellationToken = default)
        => await _context.Accounts.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IEnumerable<Account>> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default)
        => await _context.Accounts.AsNoTracking().Where(account => account.OwnerUserId == ownerUserId).ToListAsync(cancellationToken);

    public async Task AddAsync(Account account, CancellationToken cancellationToken = default)
        => await _context.Accounts.AddAsync(account, cancellationToken);

    public Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        _context.Accounts.Update(account);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => await _context.SaveChangesAsync(cancellationToken);
}
