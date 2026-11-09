using BankFlow.Api.Filters;
using FluentValidation;
using Microsoft.AspNetCore.Identity;

namespace BankFlow.Api.Services;

public sealed record CreateClientRequest(string Email, string Password);
public sealed record ClientResponse(string Id, string Email);

public sealed class CreateClientRequestValidator : AbstractValidator<CreateClientRequest>
{
    public CreateClientRequestValidator()
    {
        RuleFor(request => request.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(request => request.Password).NotEmpty().MinimumLength(12).MaximumLength(256);
    }
}

public static class ClientEndpoints
{
    public static void MapClients(this WebApplication app)
    {
        var clients = app.MapGroup("/clients").RequireAuthorization(BankFlowRoles.OperatorPolicy);
        clients.MapGet("", async (UserManager<IdentityUser> users) =>
            TypedResults.Ok((await users.GetUsersInRoleAsync(BankFlowRoles.Client))
                .Select(user => new ClientResponse(user.Id, user.Email!))));

        clients.MapGet("/{userId}", async Task<IResult> (string userId, UserManager<IdentityUser> users) =>
        {
            var user = await users.FindByIdAsync(userId);
            return user is not null && await users.IsInRoleAsync(user, BankFlowRoles.Client)
                ? TypedResults.Ok(new ClientResponse(user.Id, user.Email!)) : TypedResults.NotFound();
        });

        clients.MapPost("", async Task<IResult> (CreateClientRequest request, UserManager<IdentityUser> users,
            AuthDbContext db, CancellationToken cancellationToken) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var email = request.Email.Trim();
            var user = new IdentityUser { UserName = email, Email = email };
            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
            {
                if (created.Errors.Any(error => error.Code is "DuplicateUserName" or "DuplicateEmail"))
                    return TypedResults.Conflict(new { error = "Email already registered." });
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["credentials"] = created.Errors.Select(error => error.Description).ToArray()
                });
            }
            var assigned = await users.AddToRoleAsync(user, BankFlowRoles.Client);
            if (!assigned.Succeeded) return TypedResults.Problem(statusCode: 500, title: "Unable to create client.");
            await transaction.CommitAsync(cancellationToken);
            return TypedResults.Created($"/clients/{user.Id}", new ClientResponse(user.Id, user.Email!));
        }).AddEndpointFilter<ValidationFilter<CreateClientRequest>>();
    }
}