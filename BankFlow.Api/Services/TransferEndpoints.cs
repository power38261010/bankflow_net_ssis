using System.Security.Claims;
using BankFlow.Api.Filters;
using BankFlow.Application.Services;
using BankFlow.Domain;
using FluentValidation;

namespace BankFlow.Api.Services;

public sealed record CreateTransferRequest(Guid SourceAccountId, string DestinationAccountNumber, decimal Amount, string? Description);
public sealed record TransferResponse(Guid Id, Guid SourceAccountId, string DestinationAccountNumber,
    decimal Amount, string Description, DateTimeOffset CreatedAt)
{
    public static TransferResponse From(Transfer transfer) => new(transfer.Id, transfer.SourceAccountId,
        transfer.DestinationAccountNumber, transfer.Amount, transfer.Description, transfer.CreatedAt);
}

public sealed class CreateTransferRequestValidator : AbstractValidator<CreateTransferRequest>
{
    public CreateTransferRequestValidator()
    {
        RuleFor(request => request.SourceAccountId).NotEmpty();
        RuleFor(request => request.DestinationAccountNumber).NotEmpty().Length(4, 20).Matches("^[a-zA-Z0-9-]+$");
        RuleFor(request => request.Amount).GreaterThan(0).LessThanOrEqualTo(1000000m).PrecisionScale(18, 2, true);
        RuleFor(request => request.Description).MaximumLength(140);
    }
}

public static class TransferEndpoints
{
    public static void MapTransfers(this WebApplication app)
    {
        var transfers = app.MapGroup("/transfers").RequireAuthorization(BankFlowRoles.BankingPolicy);
        transfers.MapPost("", async Task<IResult> (CreateTransferRequest request, HttpContext context,
            TransferService service, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var header = context.Request.Headers["Idempotency-Key"];
            if (header.Count != 1 || !Guid.TryParse(header[0], out var key) || key == Guid.Empty)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["A UUID idempotency key is required."] });
            try
            {
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                var outcome = await service.ExecuteAsync(request.SourceAccountId, request.DestinationAccountNumber,
                    request.Amount, request.Description, actor, context.User.IsInRole(BankFlowRoles.Operator), key, cancellationToken);
                loggerFactory.CreateLogger("BankFlow.Transfers").LogInformation(
                    "Transfer committed: {TransferId}, actor {ActorUserId}, replayed {Replayed}", outcome.Transfer.Id, actor, outcome.Replayed);
                var response = TransferResponse.From(outcome.Transfer);
                return outcome.Replayed ? TypedResults.Ok(response) : TypedResults.Created($"/transfers/{response.Id}", response);
            }
            catch (TransferConflictException exception)
            {
                return TypedResults.Conflict(new { code = exception.Code });
            }
        }).AddEndpointFilter<ValidationFilter<CreateTransferRequest>>().WithSummary("Transfer funds atomically. Requires Idempotency-Key UUID header.");

        transfers.MapGet("/{transferId:guid}", async Task<IResult> (Guid transferId, HttpContext context,
            TransferService service, CancellationToken cancellationToken) =>
        {
            var receipt = await service.GetReceiptAsync(transferId, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                context.User.IsInRole(BankFlowRoles.Operator), cancellationToken);
            return receipt is null ? TypedResults.NotFound() : TypedResults.Ok(TransferResponse.From(receipt));
        });
    }
}