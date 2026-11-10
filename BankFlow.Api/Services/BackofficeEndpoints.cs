using BankFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Api.Services;

public sealed record BackofficeOverviewResponse(
    int AccountCount,
    decimal TotalBalance,
    int ClientCount,
    int TransfersLast30Days,
    decimal TransferVolumeLast30Days,
    int PendingPayments,
    int FailedPayments,
    decimal DepositsLast30Days,
    decimal WithdrawalsLast30Days,
    IReadOnlyList<BackofficeTransferResponse> RecentTransfers);

public sealed record BackofficeTransferResponse(
    Guid Id,
    string SourceAccountLastFour,
    string DestinationAccountLastFour,
    decimal Amount,
    DateTimeOffset CreatedAt);

public static class BackofficeEndpoints
{
    public static void MapBackoffice(this WebApplication app)
    {
        var backoffice = app.MapGroup("/backoffice").RequireAuthorization(BankFlowRoles.BackofficePolicy);
        backoffice.MapGet("/overview", async Task<IResult> (
            BankFlowDbContext db,
            AuthDbContext authDb,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            var sinceUtc = since.UtcDateTime;
            var totalBalance = await SumAmountsAsync(db, db.Accounts.Select(account => account.Balance), cancellationToken);
            var accountCount = await db.Accounts.CountAsync(cancellationToken);
            var clientRoleId = await authDb.Roles.AsNoTracking()
                .Where(role => role.Name == BankFlowRoles.Client)
                .Select(role => role.Id)
                .SingleAsync(cancellationToken);
            var clientCount = await authDb.UserRoles.CountAsync(role => role.RoleId == clientRoleId, cancellationToken);
            int transferCount;
            decimal transferVolume;
            BackofficeTransferResponse[] recent;
            if (db.Database.IsSqlite())
            {
                // SQLite cannot compare/order DateTimeOffset; keep this provider-specific fallback exact.
                var allTransfers = await db.Transfers.AsNoTracking().ToArrayAsync(cancellationToken);
                var periodTransfers = allTransfers.Where(transfer => transfer.CreatedAt >= since).ToArray();
                transferCount = periodTransfers.Length;
                transferVolume = periodTransfers.Sum(transfer => transfer.Amount);
                var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(account => account.Id, cancellationToken);
                recent = allTransfers.OrderByDescending(transfer => transfer.CreatedAt).Take(8)
                    .Select(transfer => new BackofficeTransferResponse(
                        transfer.Id,
                        MaskLastFour(accounts[transfer.SourceAccountId].AccountNumber),
                        MaskLastFour(accounts[transfer.DestinationAccountId].AccountNumber),
                        transfer.Amount,
                        transfer.CreatedAt))
                    .ToArray();
            }
            else
            {
                var transfers = db.Transfers.Where(transfer => transfer.CreatedAt >= since);
                transferCount = await transfers.CountAsync(cancellationToken);
                transferVolume = await SumAmountsAsync(db, transfers.Select(transfer => transfer.Amount), cancellationToken);
                recent = await db.Transfers.AsNoTracking()
                    .Join(db.Accounts.AsNoTracking(), transfer => transfer.SourceAccountId, account => account.Id,
                        (transfer, source) => new { transfer, source.AccountNumber })
                    .Join(db.Accounts.AsNoTracking(), item => item.transfer.DestinationAccountId, account => account.Id,
                        (item, destination) => new
                        {
                            item.transfer.Id,
                            item.transfer.CreatedAt,
                            item.transfer.Amount,
                            SourceAccountNumber = item.AccountNumber,
                            DestinationAccountNumber = destination.AccountNumber
                        })
                    .OrderByDescending(transfer => transfer.CreatedAt)
                    .Take(8)
                    .Select(transfer => new BackofficeTransferResponse(
                        transfer.Id,
                        transfer.SourceAccountNumber.Length <= 4
                            ? transfer.SourceAccountNumber
                            : transfer.SourceAccountNumber.Substring(transfer.SourceAccountNumber.Length - 4),
                        transfer.DestinationAccountNumber.Length <= 4
                            ? transfer.DestinationAccountNumber
                            : transfer.DestinationAccountNumber.Substring(transfer.DestinationAccountNumber.Length - 4),
                        transfer.Amount,
                        transfer.CreatedAt))
                    .ToArrayAsync(cancellationToken);
            }
            var pendingPayments = await db.ScheduledPayments.CountAsync(payment =>
                payment.Status == BankFlow.Domain.PaymentStatus.Pending ||
                payment.Status == BankFlow.Domain.PaymentStatus.Processing, cancellationToken);
            var failedPayments = await db.ScheduledPayments.CountAsync(payment =>
                payment.Status == BankFlow.Domain.PaymentStatus.Failed, cancellationToken);

            var deposits = await SumAmountsAsync(db, db.Transactions
                .Where(transaction => transaction.CreatedAt >= sinceUtc && transaction.Type == "Deposit")
                .Select(transaction => transaction.Amount), cancellationToken);
            var withdrawals = await SumAmountsAsync(db, db.Transactions
                .Where(transaction => transaction.CreatedAt >= sinceUtc && transaction.Type == "Withdrawal")
                .Select(transaction => transaction.Amount), cancellationToken);

            return TypedResults.Ok(new BackofficeOverviewResponse(
                accountCount,
                totalBalance,
                clientCount,
                transferCount,
                transferVolume,
                pendingPayments,
                failedPayments,
                deposits,
                withdrawals,
                recent));
        }).WithSummary("CEO/operator read-only overview; sensitive account numbers are masked.");
    }

    private static async Task<decimal> SumAmountsAsync(
        BankFlowDbContext db,
        IQueryable<decimal> amounts,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlite()) return await amounts.SumAsync(cancellationToken);
        // SQLite cannot aggregate decimal exactly; avoid a lossy double conversion.
        return (await amounts.ToArrayAsync(cancellationToken)).Sum();
    }

    private static string MaskLastFour(string accountNumber) =>
        accountNumber.Length <= 4 ? accountNumber : accountNumber[^4..];
}
