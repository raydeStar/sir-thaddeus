using SirThaddeus.Config;

namespace SirThaddeus.Tests;

public sealed class LocationPolicyTests
{
    [Fact]
    public void AppSettings_EffectiveLocation_UsesUserProfileLocation()
    {
        var settings = new AppSettings
        {
            UserProfile = new UserProfileSettings
            {
                Location = new LocationSettings
                {
                    Mode = "manual",
                    Value = "Portland, OR",
                    UpdatedAt = "2026-02-16T00:00:00.0000000Z"
                }
            },
            Location = new LocationSettings
            {
                Enabled = true,
                Label = "Legacy Location"
            }
        };

        var effective = settings.GetEffectiveUserLocation();

        Assert.Equal("manual", effective.GetNormalizedMode());
        Assert.Equal("Portland, OR", effective.GetResolvedLabel());
    }

    [Fact]
    public void SourceTree_DoesNotReferenceDeviceGeolocationApis()
    {
        var repoRoot = FindRepoRoot();
        var roots = new[]
        {
            Path.Combine(repoRoot, "apps", "ui-avalonia"),
            Path.Combine(repoRoot, "packages", "local-tools"),
            Path.Combine(repoRoot, "apps", "mcp-server")
        };

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".js", ".jsx", ".ts", ".tsx", ".py"
        };

        var forbiddenTokens = new[]
        {
            "Windows.Devices.Geolocation",
            "GeoCoordinateWatcher",
            "navigator.geolocation",
            "getCurrentPosition",
            "watchPosition"
        };

        var violations = new List<string>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!allowedExtensions.Contains(Path.GetExtension(file)))
                    continue;

                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (var token in forbiddenTokens)
                {
                    if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"{Path.GetRelativePath(repoRoot, file)} :: {token}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Forbidden geolocation API references found:\n" + string.Join("\n", violations));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var slnPath = Path.Combine(directory.FullName, "SirThaddeus.sln");
            if (File.Exists(slnPath))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
