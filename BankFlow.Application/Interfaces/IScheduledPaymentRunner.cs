namespace BankFlow.Application.Interfaces;

public sealed record PaymentClaim(Guid PaymentId, Guid LeaseToken);
public interface IPaymentExecutionAuthorizer
{
    Task<bool> CanExecuteAsync(string creatorUserId, string ownerUserId, CancellationToken cancellationToken);
}
public interface IScheduledPaymentRunner
{
    Task<PaymentClaim?> ClaimNextAsync(CancellationToken cancellationToken);
    Task ExecuteAsync(PaymentClaim claim, CancellationToken cancellationToken);
}