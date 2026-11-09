using BankFlow.Domain;

namespace BankFlow.Application.Interfaces;

public sealed record NotificationItem(Guid Id, string Type, string Payload, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, int Page, int PageSize, int TotalCount, int UnreadCount);

public interface INotificationStore
{
    Task<NotificationPage> PageAsync(string ownerUserId, int page, int pageSize, CancellationToken cancellationToken);
    Task<bool> MarkReadAsync(string ownerUserId, Guid notificationId, CancellationToken cancellationToken);
}

public interface IOutboxPublisher
{
    Task<int> ProcessBatchAsync(int maximum, CancellationToken cancellationToken);
}

public interface INotificationRealtimeSink
{
    Task NotifyAvailableAsync(string recipientUserId, Guid notificationId, CancellationToken cancellationToken);
}