using BankFlow.Application.Interfaces;
using BankFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Infrastructure.Persistence;

public sealed class SqlBankingReadQueries(BankFlowDbContext db) : IBankingReadQueries
{
    public async Task<PageResult<AccountListItem>> AccountsAsync(int page, int pageSize, string? search,
        string? ownerUserId, CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize);
        var query = VisibleAccounts(ownerUserId);
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            if (term.Length > 100) throw new ArgumentException("Search is too long.");
            query = query.Where(account => account.CustomerName.Contains(term) || account.AccountNumber.Contains(term));
        }
        var total = await query.TagWith("BankFlow.AccountPage.Count").CountAsync(cancellationToken);
        var items = await query.TagWith("BankFlow.AccountPage.Items")
            .OrderByDescending(account => account.CreatedAt).ThenByDescending(account => account.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(account => new AccountListItem(account.Id, account.CustomerName, account.AccountNumber,
                account.Balance, account.CreatedAt, account.OwnerUserId)).ToListAsync(cancellationToken);
        return new PageResult<AccountListItem>(items, page, pageSize, total);
    }

    public async Task<AccountSummary> SummaryAsync(string? ownerUserId, CancellationToken cancellationToken = default)
    {
        var summary = await VisibleAccounts(ownerUserId).TagWith("BankFlow.AccountSummary")
            .GroupBy(account => 1)
            .Select(group => new AccountSummary(group.Count(), group.Sum(account => account.Balance)))
            .SingleOrDefaultAsync(cancellationToken);
        return summary ?? new AccountSummary(0, 0m);
    }

    public async Task<PageResult<MovementListItem>?> MovementsAsync(Guid accountId, int page, int pageSize,
        string? ownerUserId, CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize);
        if (!await VisibleAccounts(ownerUserId).TagWith("BankFlow.MovementPage.Access")
            .AnyAsync(account => account.Id == accountId, cancellationToken)) return null;
        var query = db.Transactions.AsNoTracking().Where(movement => movement.AccountId == accountId);
        var total = await query.TagWith("BankFlow.MovementPage.Count").CountAsync(cancellationToken);
        var items = await query.TagWith("BankFlow.MovementPage.Items")
            .OrderByDescending(movement => movement.CreatedAt).ThenByDescending(movement => movement.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(movement => new MovementListItem(movement.Id, movement.AccountId, movement.Amount,
                movement.Type, movement.Description, movement.CreatedAt, movement.TransferId)).ToListAsync(cancellationToken);
        return new PageResult<MovementListItem>(items, page, pageSize, total);
    }

    private IQueryable<Account> VisibleAccounts(string? ownerUserId)
    {
        var query = db.Accounts.AsNoTracking();
        return ownerUserId is null ? query : query.Where(account => account.OwnerUserId == ownerUserId);
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
            throw new ArgumentException("Page must be 1..10000 and pageSize 1..100.");
    }
}