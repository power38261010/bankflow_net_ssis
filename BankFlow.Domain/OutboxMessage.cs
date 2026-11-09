namespace BankFlow.Domain;

public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public string RecipientUserId { get; private set; } = string.Empty;
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public Guid AggregateId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? RealtimeSentAt { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public int Attempts { get; private set; }
    public Guid Version { get; private set; }

    private OutboxMessage() { }

    public OutboxMessage(string recipientUserId, string type, string payload, Guid aggregateId, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(recipientUserId) || recipientUserId.Length > 450) throw new ArgumentException("Notification recipient is required.");
        if (string.IsNullOrWhiteSpace(type) || type.Length > 80 || payload.Length > 2000) throw new ArgumentException("Notification payload is invalid.");
        Id = Guid.NewGuid(); RecipientUserId = recipientUserId; Type = type; Payload = payload;
        AggregateId = aggregateId; OccurredAt = occurredAt; Version = Guid.NewGuid();
    }

    public bool TryClaim(DateTimeOffset now, TimeSpan duration, bool retryRealtimeDelivery = false)
    {
        if (ProcessedAt is not null && (!retryRealtimeDelivery || RealtimeSentAt is not null) || LeaseUntil > now) return false;
        LeaseUntil = now.Add(duration); Attempts++; Version = Guid.NewGuid(); return true;
    }

    public void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt ??= now; LeaseUntil = null; Version = Guid.NewGuid();
    }

    public void MarkRealtimeSent(DateTimeOffset now)
    {
        RealtimeSentAt ??= now; LeaseUntil = null; Version = Guid.NewGuid();
    }

    public void Release(DateTimeOffset retryAt)
    {
        LeaseUntil = retryAt; Version = Guid.NewGuid();
    }
}