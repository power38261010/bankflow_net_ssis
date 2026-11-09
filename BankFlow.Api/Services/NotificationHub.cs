using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace BankFlow.Api.Services;

[Authorize(Policy = BankFlowRoles.BankingPolicy)]
public sealed class NotificationHub(IOptions<IdentityOptions> identityOptions) : Hub
{
    public static string Group(string userId, string securityStamp) => $"user:{userId}:{securityStamp}";

    public static bool IsAllowedOrigin(string? originHeader, string requestScheme, string requestHost)
    {
        if (string.IsNullOrWhiteSpace(originHeader)) return true;
        return Uri.TryCreate(originHeader, UriKind.Absolute, out var origin) &&
            string.Equals(origin.Authority, requestHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase);
    }

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var originHeader = http?.Request.Headers.Origin.ToString();
        if (!IsAllowedOrigin(originHeader, http?.Request.Scheme ?? string.Empty, http?.Request.Host.Value ?? string.Empty))
        {
            Context.Abort(); return;
        }
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var securityStamp = Context.User?.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(securityStamp)) { Context.Abort(); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(userId, securityStamp), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRNotificationSink(Microsoft.AspNetCore.SignalR.IHubContext<NotificationHub> hub,
    UserManager<IdentityUser> users)
    : BankFlow.Application.Interfaces.INotificationRealtimeSink
{
    public async Task NotifyAvailableAsync(string recipientUserId, Guid notificationId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(recipientUserId);
        if (user is null) return;
        var stamp = await users.GetSecurityStampAsync(user);
        await hub.Clients.Group(NotificationHub.Group(recipientUserId, stamp)).SendAsync("notificationAvailable", notificationId, cancellationToken);
    }
}

public sealed class NotificationOutboxWorker(IServiceScopeFactory scopes, ILogger<NotificationOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var publisher = scope.ServiceProvider.GetRequiredService<BankFlow.Application.Interfaces.IOutboxPublisher>();
                    if (await publisher.ProcessBatchAsync(50, stoppingToken) == 0) await timer.WaitForNextTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Notification outbox processing failed.");
                    await timer.WaitForNextTickAsync(stoppingToken);
                }
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}