using System.Security.Claims;
using BankFlow.Application.Interfaces;
using BankFlow.Application.Services;
using BankFlow.Api.Filters;
using BankFlow.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Identity;

namespace BankFlow.Api.Services;

public sealed record AccountResponse(Guid Id, string CustomerName, string AccountNumber, decimal Balance,
    DateTimeOffset CreatedAt, string? OwnerUserId)
{
    public static AccountResponse From(Account account) => new(account.Id, account.CustomerName,
        account.AccountNumber, account.Balance, new DateTimeOffset(DateTime.SpecifyKind(account.CreatedAt, DateTimeKind.Utc)), account.OwnerUserId);
}

public sealed record AssignAccountOwnerRequest(string OwnerUserId);
public sealed record PageRequest(int Page = 1, int PageSize = 20, string? Search = null);
public sealed record MovementResponse(Guid Id, Guid AccountId, decimal Amount, string Type,
    string Description, DateTimeOffset CreatedAt, Guid? TransferId);

public sealed class AssignAccountOwnerRequestValidator : AbstractValidator<AssignAccountOwnerRequest>
{
    public AssignAccountOwnerRequestValidator()
    {
        RuleFor(request => request.OwnerUserId).NotEmpty().MaximumLength(450);
    }
}

public sealed class AccountAccessFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var principal = context.HttpContext.User;
        if (principal.IsInRole(BankFlowRoles.Operator)) return await next(context);
        var accountId = Guid.Parse(context.HttpContext.Request.RouteValues["accountId"]!.ToString()!);
        var repository = context.HttpContext.RequestServices.GetRequiredService<IAccountRepository>();
        var account = await repository.GetByIdAsync(accountId, context.HttpContext.RequestAborted);
        if (account is null || account.OwnerUserId != principal.FindFirstValue(ClaimTypes.NameIdentifier))
            return TypedResults.NotFound();
        return await next(context);
    }
}

public static class AccountEndpoints
{
    public static void MapAccounts(this WebApplication app)
    {
        var accounts = app.MapGroup("/accounts").RequireAuthorization(BankFlowRoles.BankingPolicy);
        accounts.MapGet("/page", async (HttpContext context, [AsParameters] PageRequest request,
            IBankingReadQueries queries, CancellationToken cancellationToken) =>
        {
            var result = await queries.AccountsAsync(request.Page, request.PageSize, request.Search, OwnerScope(context), cancellationToken);
            return TypedResults.Ok(new PageResult<AccountResponse>(result.Items.Select(item => new AccountResponse(
                item.Id, item.CustomerName, item.AccountNumber, item.Balance, Utc(item.CreatedAt), item.OwnerUserId)).ToArray(),
                result.Page, result.PageSize, result.TotalCount));
        }).WithSummary("Bounded account page, filtered by the authenticated owner.");

        accounts.MapGet("/summary", async (HttpContext context, IBankingReadQueries queries, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.SummaryAsync(OwnerScope(context), cancellationToken)));

        accounts.MapGet("/{accountId:guid}/transactions/page", async Task<IResult> (Guid accountId, HttpContext context,
            [AsParameters] PageRequest request, IBankingReadQueries queries, CancellationToken cancellationToken) =>
        {
            var result = await queries.MovementsAsync(accountId, request.Page, request.PageSize, OwnerScope(context), cancellationToken);
            return result is null ? TypedResults.NotFound() : TypedResults.Ok(new PageResult<MovementResponse>(result.Items
                .Select(item => new MovementResponse(item.Id, item.AccountId, item.Amount, item.Type, item.Description,
                    Utc(item.CreatedAt), item.TransferId)).ToArray(), result.Page, result.PageSize, result.TotalCount));
        }).WithSummary("Bounded movement page. Foreign accounts return 404.");
        accounts.MapGet("", async (HttpContext context, IAccountRepository repository, CancellationToken cancellationToken) =>
        {
            var visible = context.User.IsInRole(BankFlowRoles.Operator)
                ? await repository.GetAllAsync(cancellationToken)
                : await repository.GetByOwnerAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken);
            return TypedResults.Ok(visible.Select(AccountResponse.From));
        });

        accounts.MapGet("/{accountId:guid}", async Task<IResult> (Guid accountId, IAccountRepository repository, CancellationToken cancellationToken) =>
        {
            var account = await repository.GetByIdAsync(accountId, cancellationToken);
            return account is null ? TypedResults.NotFound() : TypedResults.Ok(AccountResponse.From(account));
        }).AddEndpointFilter<AccountAccessFilter>();

        accounts.MapPost("", async Task<IResult> (CreateAccountRequest request, BankingService service,
            UserManager<IdentityUser> users, CancellationToken cancellationToken) =>
        {
            if (!await IsClientAsync(users, request.OwnerUserId))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ownerUserId"] = ["Select a registered client."] });
            var account = await service.CreateAccountAsync(request.CustomerName, request.AccountNumber,
                cancellationToken, request.OwnerUserId);
            return TypedResults.Created($"/accounts/{account.Id}", AccountResponse.From(account));
        }).RequireAuthorization(BankFlowRoles.OperatorPolicy).AddEndpointFilter<ValidationFilter<CreateAccountRequest>>();

        accounts.MapPut("/{accountId:guid}/owner", async Task<IResult> (Guid accountId, AssignAccountOwnerRequest request,
            IAccountRepository repository, UserManager<IdentityUser> users, CancellationToken cancellationToken) =>
        {
            var account = await repository.GetByIdAsync(accountId, cancellationToken);
            if (account is null) return TypedResults.NotFound();
            if (!await IsClientAsync(users, request.OwnerUserId))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ownerUserId"] = ["Select a registered client."] });
            account.AssignOwner(request.OwnerUserId);
            await repository.UpdateAsync(account, cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(AccountResponse.From(account));
        }).RequireAuthorization(BankFlowRoles.OperatorPolicy).AddEndpointFilter<ValidationFilter<AssignAccountOwnerRequest>>();

        accounts.MapPost("/{accountId:guid}/deposit", async (Guid accountId, DepositRequest request,
            BankingService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(AccountResponse.From(await service.DepositAsync(accountId, request.Amount, cancellationToken))))
            .RequireAuthorization(BankFlowRoles.OperatorPolicy).AddEndpointFilter<ValidationFilter<DepositRequest>>();

        accounts.MapPost("/{accountId:guid}/withdraw", async (Guid accountId, DepositRequest request,
            BankingService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(AccountResponse.From(await service.WithdrawAsync(accountId, request.Amount, cancellationToken))))
            .RequireAuthorization(BankFlowRoles.OperatorPolicy).AddEndpointFilter<ValidationFilter<DepositRequest>>();

        accounts.MapGet("/{accountId:guid}/transactions", async (Guid accountId, BankingService service, CancellationToken cancellationToken) =>
        {
            var transactions = await service.GetTransactionsAsync(accountId, cancellationToken);
            return TypedResults.Ok(transactions.Select(transaction => new
            {
                transaction.Id, transaction.AccountId, transaction.Amount, transaction.Type,
                transaction.Description, transaction.CreatedAt, transaction.TransferId
            }));
        }).AddEndpointFilter<AccountAccessFilter>();
    }

    private static async Task<bool> IsClientAsync(UserManager<IdentityUser> users, string userId)
    {
        var user = await users.FindByIdAsync(userId);
        return user is not null && await users.IsInRoleAsync(user, BankFlowRoles.Client);
    }

    private static string? OwnerScope(HttpContext context) => context.User.IsInRole(BankFlowRoles.Operator)
        ? null : context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static DateTimeOffset Utc(DateTime timestamp) => new(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
}