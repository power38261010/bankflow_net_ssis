using BankFlow.Application.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace BankFlow.Api.Services;

public sealed class IdentityPaymentAuthorizer(UserManager<IdentityUser> users) : IPaymentExecutionAuthorizer
{
    public async Task<bool> CanExecuteAsync(string creatorUserId, string ownerUserId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = await users.FindByIdAsync(ownerUserId);
        var creator = await users.FindByIdAsync(creatorUserId);
        return owner is not null && creator is not null && !await users.IsLockedOutAsync(owner) && !await users.IsLockedOutAsync(creator) &&
            await users.IsInRoleAsync(owner, BankFlowRoles.Client) &&
            (creator.Id == owner.Id || await users.IsInRoleAsync(creator, BankFlowRoles.Operator));
    }
}

public sealed class ScheduledPaymentWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ScheduledPaymentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        try
        {
            do
            {
                for (var processed = 0; processed < 10 && !stoppingToken.IsCancellationRequested; processed++)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var runner = scope.ServiceProvider.GetRequiredService<IScheduledPaymentRunner>();
                        var claim = await runner.ClaimNextAsync(stoppingToken);
                        if (claim is null) break;
                        await runner.ExecuteAsync(claim, stoppingToken);
                        logger.LogInformation("Scheduled payment processed: {PaymentId}", claim.PaymentId);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        logger.LogWarning("Scheduled payment iteration failed: {ErrorType}", exception.GetType().Name);
                        break;
                    }
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}