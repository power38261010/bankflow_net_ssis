namespace BankFlow.Domain;

public enum PaymentFrequency { Once, Weekly, Monthly }
public enum PaymentStatus { Pending, Processing, Completed, Cancelled, Failed }

public sealed class ScheduledPayment
{
    public Guid Id { get; private set; }
    public Guid CreationKey { get; private set; }
    public Guid? BeneficiaryId { get; private set; }
    public DateTimeOffset FirstRunAt { get; private set; }
    public string OwnerUserId { get; private set; } = string.Empty;
    public string CreatedByUserId { get; private set; } = string.Empty;
    public Guid SourceAccountId { get; private set; }
    public string DestinationAccountNumber { get; private set; } = string.Empty;
    public string BeneficiaryName { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public PaymentFrequency Frequency { get; private set; }
    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;
    public DateTimeOffset NextRunAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public Guid? LastTransferId { get; private set; }
    public Guid OccurrenceKey { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public string? FailureCode { get; private set; }
    public int Attempts { get; private set; }
    public int AnchorDay { get; private set; }
    public Guid Version { get; private set; }

    private ScheduledPayment() { }
    public ScheduledPayment(string ownerUserId, string createdByUserId, Guid sourceAccountId, Beneficiary beneficiary,
        decimal amount, string? description, PaymentFrequency frequency, DateTimeOffset dueAt, DateTimeOffset now, Guid creationKey = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId) || ownerUserId.Length > 450 || string.IsNullOrWhiteSpace(createdByUserId) || createdByUserId.Length > 450 ||
            beneficiary.OwnerUserId != createdByUserId) throw new ArgumentException("Payment identity is required.");
        if (sourceAccountId == Guid.Empty || !beneficiary.Active) throw new ArgumentException("Invalid payment accounts.");
        if (amount <= 0 || amount > 1000000m || decimal.Round(amount, 2) != amount) throw new ArgumentException("Invalid amount.");
        if (!Enum.IsDefined(frequency) || dueAt <= now || dueAt > now.AddYears(1)) throw new ArgumentException("Invalid payment date or frequency.");
        if ((description?.Trim().Length ?? 0) > 140) throw new ArgumentException("Description is too long.");
        Id = Guid.NewGuid(); OwnerUserId = ownerUserId; CreatedByUserId = createdByUserId; SourceAccountId = sourceAccountId;
        CreationKey = creationKey == Guid.Empty ? Guid.NewGuid() : creationKey; BeneficiaryId = beneficiary.Id; FirstRunAt = dueAt.ToUniversalTime();
        DestinationAccountNumber = beneficiary.AccountNumber; BeneficiaryName = beneficiary.Name; Amount = amount;
        Description = description?.Trim() ?? string.Empty; Frequency = frequency; NextRunAt = dueAt.ToUniversalTime();
        AnchorDay = NextRunAt.Day; CreatedAt = now; OccurrenceKey = Guid.NewGuid(); Version = Guid.NewGuid();
        NextAttemptAt = NextRunAt;
    }

    public bool TryClaim(DateTimeOffset now, TimeSpan duration)
    {
        if (NextRunAt > now || NextAttemptAt > now || Status is not (PaymentStatus.Pending or PaymentStatus.Processing) ||
            Status == PaymentStatus.Processing && LeaseUntil > now) return false;
        if (duration <= TimeSpan.Zero) throw new ArgumentException("Invalid lease duration.");
        Status = PaymentStatus.Processing; LeaseToken = Guid.NewGuid(); LeaseUntil = now.Add(duration);
        Attempts++; Version = Guid.NewGuid(); return true;
    }

    public bool Matches(Guid sourceId, Guid beneficiaryId, decimal amount, string? description, PaymentFrequency frequency, DateTimeOffset dueAt) =>
        SourceAccountId == sourceId && BeneficiaryId == beneficiaryId && Amount == amount && Description == (description?.Trim() ?? string.Empty) &&
        Frequency == frequency && FirstRunAt == dueAt.ToUniversalTime();

    public void RequireLease(Guid token, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Processing || LeaseToken != token || LeaseUntil <= now)
            throw new InvalidOperationException("Payment lease expired or changed.");
    }

    public void Complete(Guid token, Guid transferId, DateTimeOffset now)
    {
        RequireLease(token, now);
        LastTransferId = transferId; LastRunAt = now; FailureCode = null; LeaseToken = null; LeaseUntil = null;
        if (Frequency == PaymentFrequency.Once) Status = PaymentStatus.Completed;
        else
        {
            var next = NextRunAt;
            do
            {
                if (Frequency == PaymentFrequency.Weekly) next = next.AddDays(7);
                else
                {
                    var month = next.AddMonths(1);
                    next = new DateTimeOffset(month.Year, month.Month, Math.Min(AnchorDay, DateTime.DaysInMonth(month.Year, month.Month)),
                        next.Hour, next.Minute, next.Second, TimeSpan.Zero);
                }
            } while (next <= now);
            NextRunAt = next; Status = PaymentStatus.Pending; OccurrenceKey = Guid.NewGuid(); Attempts = 0;
            NextAttemptAt = next;
        }
        Version = Guid.NewGuid();
    }

    public void Fail(Guid token, string code, DateTimeOffset now, bool retry)
    {
        RequireLease(token, now);
        FailureCode = code; LeaseToken = null; LeaseUntil = null;
        if (retry && Attempts < 5) { Status = PaymentStatus.Pending; NextAttemptAt = now.AddMinutes(1); }
        else Status = PaymentStatus.Failed;
        Version = Guid.NewGuid();
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Pending or PaymentStatus.Processing) || Status == PaymentStatus.Processing && LeaseUntil > now)
            throw new InvalidOperationException("Payment cannot be cancelled in its current state.");
        Status = PaymentStatus.Cancelled; LeaseToken = null; LeaseUntil = null; Version = Guid.NewGuid();
    }
}