namespace BankFlow.Api;

public sealed record CreateAccountRequest(string CustomerName, string AccountNumber, string OwnerUserId);
public record DepositRequest(decimal Amount);
public sealed record LoginRequest(string Username, string Password);