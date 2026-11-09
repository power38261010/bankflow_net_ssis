namespace BankFlow.Application.Interfaces;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
public sealed record AccountListItem(Guid Id, string CustomerName, string AccountNumber, decimal Balance,
    DateTime CreatedAt, string? OwnerUserId);
public sealed record MovementListItem(Guid Id, Guid AccountId, decimal Amount, string Type,
    string Description, DateTime CreatedAt, Guid? TransferId);
public sealed record AccountSummary(int AccountCount, decimal TotalBalance);

public interface IBankingReadQueries
{
    Task<PageResult<AccountListItem>> AccountsAsync(int page, int pageSize, string? search, string? ownerUserId,
        CancellationToken cancellationToken = default);
    Task<AccountSummary> SummaryAsync(string? ownerUserId, CancellationToken cancellationToken = default);
    Task<PageResult<MovementListItem>?> MovementsAsync(Guid accountId, int page, int pageSize,
        string? ownerUserId, CancellationToken cancellationToken = default);
}