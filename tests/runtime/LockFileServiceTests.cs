using Thaddeus.Runtime.Hosting;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class LockFileServiceTests : IDisposable
{
    private readonly string _tempPath;

    public LockFileServiceTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"thaddeus-test-{Guid.NewGuid():N}.lock");
    }

    public void Dispose()
    {
        LockFileService.TryDelete(_tempPath);
    }

    [Fact]
    public void Round_trip_preserves_payload()
    {
        var payload = new RuntimeLockFile
        {
            Pid = 4242,
            Port = 51234,
            Token = "abc123",
            Version = "0.1.0",
            IpcEndpoint = "thaddeus-test",
            StartedAt = DateTimeOffset.UtcNow,
            SidecarPids = new[] { 1, 2, 3 },
        };

        LockFileService.Write(_tempPath, payload);
        var read = LockFileService.TryRead(_tempPath);

        Assert.NotNull(read);
        Assert.Equal(payload.Pid, read!.Pid);
        Assert.Equal(payload.Port, read.Port);
        Assert.Equal(payload.Token, read.Token);
        Assert.Equal(payload.Version, read.Version);
        Assert.Equal(payload.IpcEndpoint, read.IpcEndpoint);
        Assert.Equal(payload.SidecarPids, read.SidecarPids);
    }

    [Fact]
    public void TryRead_returns_null_for_missing_file()
    {
        Assert.Null(LockFileService.TryRead(_tempPath));
    }
}
