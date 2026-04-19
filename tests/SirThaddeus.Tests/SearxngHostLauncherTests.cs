using System.Reflection;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Tests;

public sealed class SearxngHostLauncherTests
{
    [Fact]
    public void EnumerateBundledPythonCandidates_ReturnsOnlyLocalRuntimePath()
    {
        var root = Path.Combine("C:\\repo", "apps", "searxng", "package");

        var method = typeof(SearxngHostLauncher).GetMethod(
            "EnumerateBundledPythonCandidates",
            BindingFlags.NonPublic | BindingFlags.Static);

        var candidates = Assert.IsAssignableFrom<IEnumerable<string>>(method!.Invoke(null, [root]));
        var resolved = candidates.ToArray();

        Assert.Single(resolved);
        Assert.Equal(Path.Combine(root, "runtime", "python", "python.exe"), resolved[0]);
    }

    [Fact]
    public void HasUsableBundledScriptPayload_RequiresLocalBundledPythonRuntime()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SirThaddeusTests", Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(tempRoot, "apps", "searxng", "package");
        var externalVoiceRuntime = Path.Combine(tempRoot, "apps", "voice-backend", "runtime", "python");

        try
        {
            Directory.CreateDirectory(Path.Combine(packageRoot, "deps", "site-packages"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "source", "searxng-upstream", "searx"));
            Directory.CreateDirectory(externalVoiceRuntime);

            File.WriteAllText(Path.Combine(packageRoot, "start-searxng.ps1"), "# test");
            File.WriteAllText(Path.Combine(packageRoot, "settings.template.yml"), "secret: test");
            File.WriteAllText(Path.Combine(packageRoot, "source", "searxng-upstream", "searx", "webapp.py"), "print('ok')");
            File.WriteAllText(Path.Combine(externalVoiceRuntime, "python.exe"), string.Empty);

            var method = typeof(SearxngHostLauncher).GetMethod(
                "HasUsableBundledScriptPayload",
                BindingFlags.NonPublic | BindingFlags.Static);

            var result = Assert.IsType<bool>(method!.Invoke(null, [packageRoot]));

            Assert.False(result);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}