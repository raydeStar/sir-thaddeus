using Thaddeus.Runtime.Api;

namespace Thaddeus.Runtime.Tests;

public sealed class RuntimeLogsApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "thaddeus-runtime-logs-tests", Guid.NewGuid().ToString("N"));

    public RuntimeLogsApiTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ListRecent_ReturnsOnlySupportedLogFiles_NewestFirst()
    {
        var olderLogPath = Path.Combine(_root, "thaddeus-runtime-20260510.log");
        var newerAuditPath = Path.Combine(_root, "audit.jsonl");
        var ignoredPath = Path.Combine(_root, "notes.txt");
        File.WriteAllLines(olderLogPath, ["first", "last runtime"]);
        File.WriteAllLines(newerAuditPath, ["{\"event\":\"one\"}"]);
        File.WriteAllText(ignoredPath, "not a runtime log");
        File.SetLastWriteTimeUtc(olderLogPath, new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerAuditPath, new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc));

        var logs = RuntimeLogsApi.ListRecent(_root, 10);

        Assert.Equal(["audit.jsonl", "thaddeus-runtime-20260510.log"], logs.Select(log => log.FileName).ToArray());
        var runtimeLog = logs.Single(log => log.FileName == "thaddeus-runtime-20260510.log");
        Assert.Equal(2, runtimeLog.LineCount);
        Assert.Equal("last runtime", runtimeLog.LastLine);
    }

    [Fact]
    public void ReadTailLines_ReturnsRequestedTailWithOriginalLineNumbers()
    {
        var logPath = Path.Combine(_root, "thaddeus-runtime-20260511.log");
        File.WriteAllLines(logPath, ["one", "two", "three", "four"]);

        var lines = RuntimeLogsApi.ReadTailLines(logPath, 2);

        Assert.Equal([3, 4], lines.Select(line => line.Number).ToArray());
        Assert.Equal(["three", "four"], lines.Select(line => line.Text).ToArray());
    }

    [Theory]
    [InlineData("thaddeus-runtime-20260511.log", true)]
    [InlineData("audit.jsonl", true)]
    [InlineData("../audit.jsonl", false)]
    [InlineData("audit.json", false)]
    [InlineData("runtime log.log", false)]
    public void IsSafeLogFileName_RestrictsNamesToLocalLogFiles(string fileName, bool expected)
    {
        Assert.Equal(expected, RuntimeLogsApi.IsSafeLogFileName(fileName));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup for temp files held by antivirus or indexers.
        }
    }
}