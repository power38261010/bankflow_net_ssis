using System.Security.Claims;
using BankFlow.Api.Filters;
using BankFlow.Application.Interfaces;
using BankFlow.Application.Services;
using BankFlow.Domain;
using FluentValidation;

namespace BankFlow.Api.Services;

public sealed record CreateBeneficiaryRequest(string Name, string AccountNumber);
public sealed record CreateScheduledPaymentRequest(Guid SourceAccountId, Guid BeneficiaryId, decimal Amount,
    string? Description, PaymentFrequency Frequency, DateTimeOffset DueAt);

public sealed class CreateBeneficiaryValidator : AbstractValidator<CreateBeneficiaryRequest>
{
    public CreateBeneficiaryValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(80);
        RuleFor(request => request.AccountNumber).NotEmpty().Length(4, 20).Matches("^[a-zA-Z0-9-]+$");
    }
}
public sealed class CreateScheduledPaymentValidator : AbstractValidator<CreateScheduledPaymentRequest>
{
    public CreateScheduledPaymentValidator()
    {
        RuleFor(request => request.SourceAccountId).NotEmpty(); RuleFor(request => request.BeneficiaryId).NotEmpty();
        RuleFor(request => request.Amount).GreaterThan(0).LessThanOrEqualTo(1000000m).PrecisionScale(18, 2, true);
        RuleFor(request => request.Description).MaximumLength(140); RuleFor(request => request.Frequency).IsInEnum();
    }
}

public static class PaymentEndpoints
{
    public static void MapPayments(this WebApplication app)
    {
        var beneficiaries = app.MapGroup("/beneficiaries").RequireAuthorization(BankFlowRoles.BankingPolicy);
        beneficiaries.MapGet("", async (HttpContext context, [AsParameters] PageRequest page, IPaymentRepository repository, CancellationToken cancellationToken) =>
            TypedResults.Ok(await repository.BeneficiariesAsync(Actor(context), page.Page, page.PageSize, cancellationToken)));
        beneficiaries.MapPost("", async (CreateBeneficiaryRequest request, HttpContext context, PaymentService service, CancellationToken cancellationToken) =>
        {
            var created = await service.CreateBeneficiaryAsync(Actor(context), request.Name, request.AccountNumber, cancellationToken);
            return TypedResults.Created($"/beneficiaries/{created.Id}", new BeneficiaryItem(created.Id, created.Name, created.AccountNumber, created.CreatedAt));
        }).AddEndpointFilter<ValidationFilter<CreateBeneficiaryRequest>>();
        beneficiaries.MapGet("/{id:guid}", async Task<IResult> (Guid id, HttpContext context, IPaymentRepository repository, CancellationToken cancellationToken) =>
        {
            var item = await repository.BeneficiaryAsync(id, Actor(context), cancellationToken);
            return item is null ? TypedResults.NotFound() : TypedResults.Ok(new BeneficiaryItem(item.Id, item.Name, item.AccountNumber, item.CreatedAt));
        });
        beneficiaries.MapDelete("/{id:guid}", async (Guid id, HttpContext context, PaymentService service, CancellationToken cancellationToken) =>
        {
            await service.ArchiveBeneficiaryAsync(id, Actor(context), cancellationToken); return TypedResults.NoContent();
        });

        var payments = app.MapGroup("/scheduled-payments").RequireAuthorization(BankFlowRoles.BankingPolicy);
        payments.MapGet("", async (HttpContext context, [AsParameters] PageRequest page, IPaymentRepository repository, CancellationToken cancellationToken) =>
            TypedResults.Ok(await repository.PaymentsAsync(context.User.IsInRole(BankFlowRoles.Operator) ? null : Actor(context), page.Page, page.PageSize, cancellationToken)));
        payments.MapPost("", async Task<IResult> (CreateScheduledPaymentRequest request, HttpContext context, PaymentService service, CancellationToken cancellationToken) =>
        {
            var header = context.Request.Headers["Idempotency-Key"];
            if (header.Count != 1 || !Guid.TryParse(header[0], out var creationKey) || creationKey == Guid.Empty)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["A UUID is required."] });
            var payment = await service.CreatePaymentAsync(Actor(context), context.User.IsInRole(BankFlowRoles.Operator), request.SourceAccountId,
                request.BeneficiaryId, request.Amount, request.Description, request.Frequency, request.DueAt, creationKey, cancellationToken);
            return TypedResults.Created($"/scheduled-payments/{payment.Id}", new { payment.Id });
        }).AddEndpointFilter<ValidationFilter<CreateScheduledPaymentRequest>>();
        payments.MapGet("/{id:guid}", async Task<IResult> (Guid id, HttpContext context, IPaymentRepository repository, CancellationToken cancellationToken) =>
        {
            var payment = await repository.PaymentAsync(id, cancellationToken);
            return payment is null || !context.User.IsInRole(BankFlowRoles.Operator) && payment.OwnerUserId != Actor(context)
                ? TypedResults.NotFound() : TypedResults.Ok(new { payment.Id, payment.Status, payment.NextRunAt, payment.LastTransferId, payment.FailureCode });
        });
        payments.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext context, PaymentService service, CancellationToken cancellationToken) =>
        {
            await service.CancelPaymentAsync(id, Actor(context), context.User.IsInRole(BankFlowRoles.Operator), cancellationToken); return TypedResults.NoContent();
        });
    }
    private static string Actor(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
}