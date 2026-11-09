using System.Security.Claims;
using BankFlow.Api.Filters;
using BankFlow.Application.Interfaces;
using FluentValidation;

namespace BankFlow.Api.Services;

public sealed record NotificationPageRequest(int Page = 1, int PageSize = 20);
public sealed record MarkNotificationReadRequest(Guid Id);
public sealed class MarkNotificationReadValidator : AbstractValidator<MarkNotificationReadRequest>
{
    public MarkNotificationReadValidator() => RuleFor(request => request.Id).NotEmpty();
}

public static class NotificationEndpoints
{
    public static void MapNotifications(this WebApplication app)
    {
        var notifications = app.MapGroup("/notifications").RequireAuthorization(BankFlowRoles.BankingPolicy);
        notifications.MapGet("", async ([AsParameters] NotificationPageRequest request, HttpContext context,
            INotificationStore store, CancellationToken cancellationToken) =>
            TypedResults.Ok(await store.PageAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                request.Page, request.PageSize, cancellationToken)));
        notifications.MapPost("/read", async Task<IResult> (MarkNotificationReadRequest request, HttpContext context,
            INotificationStore store, CancellationToken cancellationToken) =>
        {
            var found = await store.MarkReadAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.Id, cancellationToken);
            return found ? TypedResults.NoContent() : TypedResults.NotFound();
        }).AddEndpointFilter<ValidationFilter<MarkNotificationReadRequest>>();
    }
}