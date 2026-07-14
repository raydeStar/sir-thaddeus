using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Tests.MCP;

public class PythonSandboxToolsTests
{
    // Integration tests only run when a Docker engine is actually reachable;
    // otherwise they no-op so CI machines without Docker stay green.
    private static readonly Lazy<bool> DockerAvailable = new(() =>
    {
        try
        {
            var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "version", "--format", "{{.Server.Os}}" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (probe is null || !probe.WaitForExit(5_000) || probe.ExitCode != 0)
                return false;

            // The sandbox image is Linux-only. A reachable Windows-container
            // daemon cannot run it and must not enable these integration tests.
            var serverOs = probe.StandardOutput.ReadToEnd().Trim();
            return string.Equals(serverOs, "linux", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    });

    [Fact]
    public void Empty_code_returns_error()
    {
        using var doc = JsonDocument.Parse(PythonSandboxTools.PythonEval("  "));
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void Oversized_code_returns_error()
    {
        using var doc = JsonDocument.Parse(PythonSandboxTools.PythonEval(new string('#', 4001)));
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("too long", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_env_returns_error_without_touching_docker()
    {
        var previous = Environment.GetEnvironmentVariable("ST_PYTHON_SANDBOX_DISABLED");
        Environment.SetEnvironmentVariable("ST_PYTHON_SANDBOX_DISABLED", "1");
        try
        {
            using var doc = JsonDocument.Parse(PythonSandboxTools.PythonEval("print(1)"));
            var error = doc.RootElement.GetProperty("error").GetString();
            Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ST_PYTHON_SANDBOX_DISABLED", previous);
        }
    }

    [Fact]
    public void Executes_script_and_returns_stdout()
    {
        if (!DockerAvailable.Value)
            return;

        using var doc = JsonDocument.Parse(PythonSandboxTools.PythonEval("print(6*7)"));
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.Equal("42", doc.RootElement.GetProperty("stdout").GetString()!.Trim());
        Assert.Equal(0, doc.RootElement.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public void Script_error_returns_traceback_the_model_can_fix()
    {
        if (!DockerAvailable.Value)
            return;

        using var doc = JsonDocument.Parse(PythonSandboxTools.PythonEval("print(undefined_name)"));
        Assert.False(doc.RootElement.TryGetProperty("error", out _)); // script ran; failure is the script's
        Assert.NotEqual(0, doc.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Contains("NameError", doc.RootElement.GetProperty("stderr").GetString());
    }

    [Fact]
    public void Network_access_is_blocked()
    {
        if (!DockerAvailable.Value)
            return;

        const string code =
            "import urllib.request\n" +
            "urllib.request.urlopen('http://example.com', timeout=2)\n" +
            "print('reached network')\n";
        using var doc = JsonDocument.Parse(PythonSandboxTools.PythonEval(code));
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.NotEqual(0, doc.RootElement.GetProperty("exit_code").GetInt32());
        Assert.DoesNotContain("reached network", doc.RootElement.GetProperty("stdout").GetString());
    }

    [Fact]
    public void Runaway_script_is_killed_by_timeout()
    {
        if (!DockerAvailable.Value)
            return;

        var previous = Environment.GetEnvironmentVariable("ST_PYTHON_SANDBOX_TIMEOUT_MS");
        Environment.SetEnvironmentVariable("ST_PYTHON_SANDBOX_TIMEOUT_MS", "1500");
        try
        {
            using var doc = JsonDocument.Parse(PythonSandboxTools.PythonEval("while True: pass"));
            var error = doc.RootElement.GetProperty("error").GetString();
            Assert.Contains("timed out", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ST_PYTHON_SANDBOX_TIMEOUT_MS", previous);
        }
    }
}
