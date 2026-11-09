using System.Diagnostics.Metrics;

namespace BankFlow.Api.Services;

public enum MfaEvent { Challenge, Verified, Rejected, Enabled, Disabled, RecoveryRegenerated }

public sealed class MfaMetrics : IDisposable
{
    private readonly Meter _meter = new("BankFlow.Security", "1.0.0");
    private readonly Counter<long> _events;

    public MfaMetrics() => _events = _meter.CreateCounter<long>("bankflow.mfa.events", description: "MFA events without sensitive user data.");
    public void Record(MfaEvent outcome) => _events.Add(1, new KeyValuePair<string, object?>("outcome", outcome.ToString()));
    public void Dispose() => _meter.Dispose();
}