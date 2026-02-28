using System.Diagnostics;
using Xunit.Abstractions;

namespace SirThaddeus.Tests;

/// <summary>
/// Lightweight helper that measures and reports wall-clock time for a test.
/// Usage: wrap the test body in <c>using var t = TestTimer.Start(output, testName);</c>
/// The elapsed time is written to the xunit output on Dispose.
/// </summary>
public sealed class TestTimer : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _label;
    private readonly Stopwatch _sw;

    private TestTimer(ITestOutputHelper output, string label)
    {
        _output = output;
        _label = label;
        _sw = Stopwatch.StartNew();
    }

    public static TestTimer Start(ITestOutputHelper output, string label)
        => new(output, label);

    public void Dispose()
    {
        _sw.Stop();
        _output.WriteLine($"[TIMING] {_label}: {_sw.Elapsed.TotalMilliseconds:F1} ms");
    }
}
