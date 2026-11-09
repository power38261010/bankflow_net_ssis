namespace BankFlow.Domain;

public sealed class Notification
{
    public Guid Id { get; private set; }
    public Guid OutboxMessageId { get; private set; }
    public string RecipientUserId { get; private set; } = string.Empty;
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    private Notification() { }
    public Notification(OutboxMessage message)
    {
        Id = Guid.NewGuid(); OutboxMessageId = message.Id; RecipientUserId = message.RecipientUserId;
        Type = message.Type; Payload = message.Payload; CreatedAt = message.OccurredAt;
    }
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}