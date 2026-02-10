using SirThaddeus.Agent;
using SirThaddeus.Config;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// Tool Group Policy Tests
//
// Tests the deterministic permission resolution logic extracted from
// WpfPermissionGate into ToolGroupPolicy (agent package):
//   - Group resolution (tool name → group)
//   - Alias canonicalization (PascalCase → same group as snake_case)
//   - Effective policy resolution (developer override, memory master off)
//   - Off hard-block returns "off" (gate never prompts)
//   - Unknown tool default (debug vs release via param)
//   - Redacted purpose strings for permission prompts
// ─────────────────────────────────────────────────────────────────────────

public class ToolGroupResolutionTests
{
    // ── Alias mapping: PascalCase and snake_case resolve identically ──

    [Theory]
    [InlineData("WebSearch",        "web")]
    [InlineData("web_search",       "web")]
    [InlineData("BrowserNavigate",  "web")]
    [InlineData("browser_navigate", "web")]
    public void KnownWebTools_MapToWebGroup(string toolName, string expected)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal(expected, group);
    }

    [Theory]
    [InlineData("MemoryRetrieve",    "memoryRead")]
    [InlineData("memory_retrieve",   "memoryRead")]
    [InlineData("MemoryListFacts",   "memoryRead")]
    [InlineData("memory_list_facts", "memoryRead")]
    public void MemoryReadTools_MapToMemoryReadGroup(string toolName, string expected)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal(expected, group);
    }

    [Theory]
    [InlineData("MemoryStoreFacts",   "memoryWrite")]
    [InlineData("memory_store_facts", "memoryWrite")]
    [InlineData("MemoryUpdateFact",   "memoryWrite")]
    [InlineData("memory_update_fact", "memoryWrite")]
    [InlineData("MemoryDeleteFact",   "memoryWrite")]
    [InlineData("memory_delete_fact", "memoryWrite")]
    public void MemoryWriteTools_MapToMemoryWriteGroup(string toolName, string expected)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal(expected, group);
    }

    [Theory]
    [InlineData("ScreenCapture",     "screen")]
    [InlineData("screen_capture",    "screen")]
    [InlineData("GetActiveWindow",   "screen")]
    [InlineData("get_active_window", "screen")]
    public void ScreenTools_MapToScreenGroup(string toolName, string expected)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal(expected, group);
    }

    [Theory]
    [InlineData("FileRead",  "files")]
    [InlineData("file_read", "files")]
    [InlineData("FileList",  "files")]
    [InlineData("file_list", "files")]
    public void FileTools_MapToFilesGroup(string toolName, string expected)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal(expected, group);
    }

    [Fact]
    public void SystemExecute_MapsToSystemGroup()
    {
        var canonical = AuditedMcpToolClient.Canonicalize("SystemExecute");
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal("system", group);
    }

    [Fact]
    public void UnknownTool_MapsToUnknownGroup()
    {
        var canonical = AuditedMcpToolClient.Canonicalize("some_future_tool");
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal("unknown", group);
    }

    [Fact]
    public void MemoryDeleteFact_RoutesToMemoryWriteGroup()
    {
        // Explicitly verifying delete is Write, not Read
        var canonical = AuditedMcpToolClient.Canonicalize("MemoryDeleteFact");
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal("memoryWrite", group);
    }

    [Theory]
    [InlineData("ToolPing",              "meta")]
    [InlineData("tool_ping",             "meta")]
    [InlineData("ToolListCapabilities",  "meta")]
    [InlineData("tool_list_capabilities","meta")]
    [InlineData("TimeNow",              "meta")]
    [InlineData("time_now",             "meta")]
    public void MetaTools_MapToMetaGroup(string toolName, string expected)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        Assert.Equal(expected, group);
    }
}

public class EffectivePolicyResolutionTests
{
    private static PolicySnapshot MakeSnapshot(
        string screen = "ask",
        string files = "ask",
        string system = "ask",
        string web = "ask",
        string memoryRead = "always",
        string memoryWrite = "ask",
        string devOverride = "none",
        bool memoryEnabled = true,
        bool isDebug = true)
    {
        return ToolGroupPolicy.BuildSnapshot(new AppSettings
        {
            Mcp = new McpSettings
            {
                Permissions = new McpPermissionsSettings
                {
                    DeveloperOverride = devOverride,
                    Screen            = screen,
                    Files             = files,
                    System            = system,
                    Web               = web,
                    MemoryRead        = memoryRead,
                    MemoryWrite       = memoryWrite
                }
            },
            Memory = new MemorySettings { Enabled = memoryEnabled }
        }, isDebugBuild: isDebug);
    }

    // ── Off hard-block ────────────────────────────────────────────────

    [Fact]
    public void OffPolicy_ReturnsOff()
    {
        var snapshot = MakeSnapshot(screen: "off");
        var effective = ToolGroupPolicy.ResolveEffectivePolicy("screen", snapshot);
        Assert.Equal("off", effective);
    }

    // ── Always auto-approve ──────────────────────────────────────────

    [Fact]
    public void AlwaysPolicy_ReturnsAlways()
    {
        var snapshot = MakeSnapshot(files: "always");
        var effective = ToolGroupPolicy.ResolveEffectivePolicy("files", snapshot);
        Assert.Equal("always", effective);
    }

    // ── Developer override wins for dangerous groups ─────────────────

    [Fact]
    public void DeveloperOverrideOff_WinsOver_PerGroupAsk_ForDangerousGroup()
    {
        var snapshot = MakeSnapshot(screen: "ask", devOverride: "off");
        var effective = ToolGroupPolicy.ResolveEffectivePolicy("screen", snapshot);
        Assert.Equal("off", effective);
    }

    [Fact]
    public void DeveloperOverrideAlways_WinsOver_PerGroupOff_ForDangerousGroup()
    {
        var snapshot = MakeSnapshot(web: "off", devOverride: "always");
        var effective = ToolGroupPolicy.ResolveEffectivePolicy("web", snapshot);
        Assert.Equal("always", effective);
    }

    [Fact]
    public void DeveloperOverride_DoesNotAffect_MemoryGroups()
    {
        var snapshot1 = MakeSnapshot(memoryRead: "always", devOverride: "off");
        Assert.Equal("always",
            ToolGroupPolicy.ResolveEffectivePolicy("memoryRead", snapshot1));

        var snapshot2 = MakeSnapshot(memoryWrite: "ask", devOverride: "off");
        Assert.Equal("ask",
            ToolGroupPolicy.ResolveEffectivePolicy("memoryWrite", snapshot2));
    }

    // ── Memory master off ────────────────────────────────────────────

    [Fact]
    public void MemoryDisabled_ForcesOff_ForMemoryGroups()
    {
        var snapshot = MakeSnapshot(
            memoryRead: "always", memoryWrite: "ask", memoryEnabled: false);

        Assert.Equal("off",
            ToolGroupPolicy.ResolveEffectivePolicy("memoryRead", snapshot));
        Assert.Equal("off",
            ToolGroupPolicy.ResolveEffectivePolicy("memoryWrite", snapshot));
    }

    [Fact]
    public void MemoryDisabled_DoesNotAffect_DangerousGroups()
    {
        var snapshot = MakeSnapshot(screen: "ask", memoryEnabled: false);
        Assert.Equal("ask",
            ToolGroupPolicy.ResolveEffectivePolicy("screen", snapshot));
    }

    // ── Meta tools always allowed ────────────────────────────────────

    [Fact]
    public void MetaGroup_AlwaysAllowed_EvenWithDevOverrideOff()
    {
        var snapshot = MakeSnapshot(devOverride: "off");
        Assert.Equal("always",
            ToolGroupPolicy.ResolveEffectivePolicy("meta", snapshot));
    }

    // ── Unknown tool default ─────────────────────────────────────────

    [Fact]
    public void UnknownGroup_InDebug_DefaultsToAsk()
    {
        var snapshot = MakeSnapshot(isDebug: true);
        Assert.Equal("ask",
            ToolGroupPolicy.ResolveEffectivePolicy("unknown", snapshot));
    }

    [Fact]
    public void UnknownGroup_InRelease_DefaultsToOff()
    {
        var snapshot = MakeSnapshot(isDebug: false);
        Assert.Equal("off",
            ToolGroupPolicy.ResolveEffectivePolicy("unknown", snapshot));
    }
}

public class RedactedPurposeTests
{
    [Fact]
    public void EmptyArguments_ProducesGenericPurpose()
    {
        var purpose = ToolGroupPolicy.BuildRedactedPurpose("screen_capture", "");
        Assert.Equal("Use tool 'screen_capture'.", purpose);
    }

    [Fact]
    public void PathArgument_IncludedInPurpose()
    {
        var args = """{"path":"C:\\Users\\Test\\file.txt"}""";
        var purpose = ToolGroupPolicy.BuildRedactedPurpose("file_read", args);
        Assert.Contains("path:", purpose);
        Assert.Contains("file.txt", purpose);
    }

    [Fact]
    public void SecretLookingValues_AreRedacted()
    {
        // Uses "command" (a safe-listed field) whose value contains a
        // secret pattern ("bearer_token_xyz"). Should be redacted.
        var args = """{"command":"bearer_token_xyz"}""";
        var purpose = ToolGroupPolicy.BuildRedactedPurpose("system_execute", args);
        Assert.Contains("[REDACTED]", purpose);
        Assert.DoesNotContain("bearer_token_xyz", purpose);
    }

    [Fact]
    public void NonSafeFields_AreNotExtracted()
    {
        // "token" is not in the safe field list, so it's not extracted
        var args = """{"token":"super_secret_value_123"}""";
        var purpose = ToolGroupPolicy.BuildRedactedPurpose("system_execute", args);
        Assert.Equal("Use tool 'system_execute'.", purpose);
    }

    [Fact]
    public void LongValues_AreTruncated()
    {
        var longPath = new string('x', 200);
        var args = $"{{\"path\":\"{longPath}\"}}";
        var purpose = ToolGroupPolicy.BuildRedactedPurpose("file_read", args);
        Assert.True(purpose.Length < 300,
            $"Purpose should be truncated, was {purpose.Length} chars");
    }

    [Fact]
    public void MalformedJson_ProducesGenericPurpose()
    {
        var purpose = ToolGroupPolicy.BuildRedactedPurpose("web_search", "not json");
        Assert.Equal("Use tool 'web_search'.", purpose);
    }
}

public class McpPermissionsSettingsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var perms = new McpPermissionsSettings();

        Assert.Equal("none",   perms.DeveloperOverride);
        Assert.Equal("ask",    perms.Screen);
        Assert.Equal("ask",    perms.Files);
        Assert.Equal("ask",    perms.System);
        Assert.Equal("ask",    perms.Web);
        Assert.Equal("always", perms.MemoryRead);
        Assert.Equal("ask",    perms.MemoryWrite);
    }

    [Fact]
    public void InvalidPolicy_NormalizesToAsk()
    {
        var snapshot = ToolGroupPolicy.BuildSnapshot(new AppSettings
        {
            Mcp = new McpSettings
            {
                Permissions = new McpPermissionsSettings
                {
                    Screen = "garbage_value"
                }
            }
        }, isDebugBuild: true);

        Assert.Equal("ask",
            ToolGroupPolicy.ResolveEffectivePolicy("screen", snapshot));
    }

    [Fact]
    public void InvalidDeveloperOverride_NormalizesToNone()
    {
        var snapshot = ToolGroupPolicy.BuildSnapshot(new AppSettings
        {
            Mcp = new McpSettings
            {
                Permissions = new McpPermissionsSettings
                {
                    DeveloperOverride = "invalid"
                }
            }
        }, isDebugBuild: true);

        // With override = none, per-group setting should apply
        Assert.Equal("ask",
            ToolGroupPolicy.ResolveEffectivePolicy("screen", snapshot));
    }
}
