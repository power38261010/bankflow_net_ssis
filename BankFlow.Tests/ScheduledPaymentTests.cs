using BankFlow.Domain;

namespace BankFlow.Tests;

public sealed class ScheduledPaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static ScheduledPayment Payment(PaymentFrequency frequency = PaymentFrequency.Once, DateTimeOffset? due = null) =>
        new("payer", "creator", Guid.NewGuid(), new Beneficiary("creator", "Destination", "DEST0001", Now),
            10m, "Rent", frequency, due ?? Now.AddMinutes(1), Now);

    [Fact]
    public void Payment_ShouldClaimOnlyWhenDueAndKeepOccurrenceKeyAcrossLeaseRecovery()
    {
        var payment = Payment(); var key = payment.OccurrenceKey;
        Assert.False(payment.TryClaim(Now, TimeSpan.FromMinutes(5)));
        Assert.True(payment.TryClaim(Now.AddMinutes(1), TimeSpan.FromMinutes(5)));
        var token = payment.LeaseToken!.Value;
        Assert.False(payment.TryClaim(Now.AddMinutes(2), TimeSpan.FromMinutes(5)));
        Assert.True(payment.TryClaim(Now.AddMinutes(7), TimeSpan.FromMinutes(5)));
        Assert.Equal(key, payment.OccurrenceKey);
        Assert.Throws<InvalidOperationException>(() => payment.Complete(token, Guid.NewGuid(), Now.AddMinutes(7)));
    }

    [Fact]
    public void Payment_ShouldCancelPendingButNotAnActiveExecution()
    {
        var pending = Payment(); pending.Cancel(Now); Assert.Equal(PaymentStatus.Cancelled, pending.Status);
        Assert.False(pending.TryClaim(Now.AddMinutes(2), TimeSpan.FromMinutes(5)));
        var active = Payment(); active.TryClaim(Now.AddMinutes(1), TimeSpan.FromMinutes(5));
        Assert.Throws<InvalidOperationException>(() => active.Cancel(Now.AddMinutes(2)));
        active.Cancel(Now.AddMinutes(7)); Assert.Equal(PaymentStatus.Cancelled, active.Status);
    }

    [Fact]
    public void Payment_ShouldPreserveMonthlyAnchorAndSkipMissedOccurrences()
    {
        var due = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var payment = Payment(PaymentFrequency.Monthly, due);
        payment.TryClaim(due, TimeSpan.FromMinutes(5)); payment.Complete(payment.LeaseToken!.Value, Guid.NewGuid(), due);
        Assert.Equal(28, payment.NextRunAt.Day);
        var february = payment.NextRunAt;
        payment.TryClaim(february, TimeSpan.FromMinutes(5)); payment.Complete(payment.LeaseToken!.Value, Guid.NewGuid(), february);
        Assert.Equal(31, payment.NextRunAt.Day);
        var late = payment.NextRunAt.AddMonths(4);
        payment.TryClaim(late, TimeSpan.FromMinutes(5)); payment.Complete(payment.LeaseToken!.Value, Guid.NewGuid(), late);
        Assert.True(payment.NextRunAt > late);
    }
}