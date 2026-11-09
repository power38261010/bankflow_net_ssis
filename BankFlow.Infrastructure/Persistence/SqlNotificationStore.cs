using BankFlow.Application.Interfaces;
using BankFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Infrastructure.Persistence;

public sealed class SqlNotificationStore(BankFlowDbContext db, TimeProvider clock) : INotificationStore
{
    public async Task<NotificationPage> PageAsync(string ownerUserId, int page, int pageSize, CancellationToken cancellationToken)
    {
        Validate(page, pageSize);
        var query = db.Notifications.AsNoTracking().Where(notification => notification.RecipientUserId == ownerUserId);
        var total = await query.CountAsync(cancellationToken);
        var unread = await query.CountAsync(notification => notification.ReadAt == null, cancellationToken);
        var items = await query.OrderByDescending(notification => notification.CreatedAt).ThenByDescending(notification => notification.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(notification => new NotificationItem(notification.Id, notification.Type, notification.Payload, notification.CreatedAt, notification.ReadAt))
            .ToListAsync(cancellationToken);
        return new NotificationPage(items, page, pageSize, total, unread);
    }

    public async Task<bool> MarkReadAsync(string ownerUserId, Guid notificationId, CancellationToken cancellationToken)
    {
        var notification = await db.Notifications.SingleOrDefaultAsync(item => item.Id == notificationId && item.RecipientUserId == ownerUserId, cancellationToken);
        if (notification is null) return false;
        notification.MarkRead(clock.GetUtcNow()); await db.SaveChangesAsync(cancellationToken); return true;
    }

    private static void Validate(int page, int pageSize)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page.");
    }
}

public sealed class SqlOutboxPublisher(BankFlowDbContext db, TimeProvider clock,
    INotificationRealtimeSink notifications) : IOutboxPublisher
{
    public async Task<int> ProcessBatchAsync(int maximum, CancellationToken cancellationToken)
    {
        if (maximum is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximum));
        var processed = 0;
        for (var index = 0; index < maximum; index++)
        {
            db.ChangeTracker.Clear();
            var now = clock.GetUtcNow();
                var message = await db.OutboxMessages.Where(candidate => (candidate.ProcessedAt == null || candidate.RealtimeSentAt == null) &&
                    (candidate.LeaseUntil == null || candidate.LeaseUntil <= now))
                .OrderBy(candidate => candidate.OccurredAt).ThenBy(candidate => candidate.Id).FirstOrDefaultAsync(cancellationToken);
            if (message is null) break;
            if (!message.TryClaim(now, TimeSpan.FromMinutes(1), retryRealtimeDelivery: message.ProcessedAt is not null)) continue;
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); continue; }

            db.ChangeTracker.Clear();
            var claimed = await db.OutboxMessages.SingleOrDefaultAsync(candidate => candidate.Id == message.Id &&
                (candidate.ProcessedAt == null || candidate.RealtimeSentAt == null) &&
                candidate.LeaseUntil > clock.GetUtcNow(), cancellationToken);
            if (claimed is null) continue;
            if (claimed.ProcessedAt is null)
            {
                var exists = await db.Notifications.AnyAsync(notification => notification.OutboxMessageId == claimed.Id, cancellationToken);
                if (!exists) db.Notifications.Add(new Notification(claimed));
            }
            claimed.MarkProcessed(clock.GetUtcNow());
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); continue; }

            processed++;
            try
            {
                await notifications.NotifyAvailableAsync(claimed.RecipientUserId, claimed.Id, cancellationToken);
                db.ChangeTracker.Clear();
                var sent = await db.OutboxMessages.SingleOrDefaultAsync(candidate => candidate.Id == claimed.Id && candidate.RealtimeSentAt == null, cancellationToken);
                if (sent is not null) { sent.MarkRealtimeSent(clock.GetUtcNow()); await db.SaveChangesAsync(cancellationToken); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                db.ChangeTracker.Clear();
                var pending = await db.OutboxMessages.SingleOrDefaultAsync(candidate => candidate.Id == claimed.Id && candidate.RealtimeSentAt == null, cancellationToken);
                if (pending is not null) { pending.Release(clock.GetUtcNow().AddSeconds(15)); try { await db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); } }
            }
        }
        return processed;
    }
}