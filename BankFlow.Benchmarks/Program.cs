using System.Text.RegularExpressions;
using BankFlow.Api.Services;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

if (args.Contains("--read-load", StringComparer.Ordinal))
    await ReadLoadScenario.RunAsync();
else
    BenchmarkSwitcher.FromAssembly(typeof(OtpValidationBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public partial class OtpValidationBenchmarks
{
    private const string Code = "123456";

    [Benchmark(Baseline = true)]
    public bool RegexValidation() => Regex.IsMatch(Code, @"\A[0-9]{6}\z", RegexOptions.CultureInvariant);

    [Benchmark]
    public bool SpanValidation() => OtpInput.IsValid(Code.AsSpan());

    [Benchmark]
    public bool SourceGeneratedValidation() => CodePattern().IsMatch(Code);

    [GeneratedRegex(@"\A[0-9]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}