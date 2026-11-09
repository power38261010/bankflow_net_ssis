using BankFlow.Application.Interfaces;
using BankFlow.Domain;
using BankFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Infrastructure.Repositories;

public sealed class SqlTransactionRepository : ITransactionRepository
{
    private readonly BankFlowDbContext _context;

    public SqlTransactionRepository(BankFlowDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
        => await _context.Transactions.AddAsync(transaction, cancellationToken);

    public async Task<IEnumerable<Transaction>> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await _context.Transactions.AsNoTracking().Where(x => x.AccountId == accountId).ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => await _context.SaveChangesAsync(cancellationToken);
}
