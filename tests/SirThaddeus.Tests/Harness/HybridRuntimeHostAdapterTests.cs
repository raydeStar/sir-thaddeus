using System.Text.Json;
using SirThaddeus.Config;
using SirThaddeus.Harness.Execution;
using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Tests.Harness;

public sealed class HybridRuntimeHostAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "thaddeus-hybrid-adapter-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Preflight_state_allows_observed_generated_fields_but_rejects_dirty_state()
    {
        using var observed = JsonDocument.Parse(
            """{"wiki":{"roots":[{"name":"Aster Archive","pages":[]}]}}""");
        using var expected = JsonDocument.Parse(
            """{"wiki":{"roots":[{"name":"Aster Archive"}]}}""");
        using var dirty = JsonDocument.Parse(
            """{"wiki":{"roots":[{"name":"Wrong Archive","pages":[]}]}}""");

        Assert.True(HybridRuntimeHostAdapter.MatchesExpectedState(
            observed.RootElement,
            expected.RootElement));
        Assert.False(HybridRuntimeHostAdapter.MatchesExpectedState(
            dirty.RootElement,
            expected.RootElement));
    }

    [Fact]
    public void WriteHarnessSettingsFile_PreservesFrozenModelParameters()
    {
        Directory.CreateDirectory(_root);
        var settings = new AppSettings
        {
            Llm = new LlmSettings
            {
                Provider = "codex-cli",
                BaseUrl = "http://127.0.0.1:1234",
                Model = "test-model",
                MaxTokens = 321,
                ContextWindowTokens = 8192,
                Temperature = 0.125,
                CodexCliPath = @"C:\tools\codex.exe",
                CodexReasoningEffort = "xhigh",
            },
            Mcp = new McpSettings
            {
                Permissions = new McpPermissionsSettings { Files = "always" }
            }
        };
        var adapter = new HybridRuntimeHostAdapter(settings);

        adapter.WriteHarnessSettingsFile(_root);

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_root, "runtime-settings.json")));
        var llm = document.RootElement.GetProperty("llm");
        Assert.Equal("codex-cli", llm.GetProperty("provider").GetString());
        Assert.Equal("test-model", llm.GetProperty("modelId").GetString());
        Assert.Equal(321, llm.GetProperty("maxTokens").GetInt32());
        Assert.Equal(8192, llm.GetProperty("contextWindowTokens").GetInt32());
        Assert.Equal(0.125, llm.GetProperty("temperature").GetDouble());
        Assert.Equal(@"C:\tools\codex.exe", llm.GetProperty("codexCliPath").GetString());
        Assert.Equal("xhigh", llm.GetProperty("codexReasoningEffort").GetString());
        var permissions = document.RootElement.GetProperty("permissions");
        Assert.Equal("always", permissions.GetProperty("files").GetString());
    }

    [Fact]
    public void HarnessTestCase_DeserializesStateSetupAndObservationScope()
    {
        const string json = """
            {
              "id": "wiki-state",
              "user_message": "Update the page.",
              "permission_decision": "once",
              "state_setup": {
                "files": [
                  { "path": "notes/input.txt", "content": "local evidence" },
                  { "path": "docs/sample.bin", "content_base64": "AAECAw==" }
                ],
                "wiki_roots": [
                  {
                    "name": "Research",
                    "pages": [{ "title": "Plan", "markdown": "before" }]
                  }
                ]
              },
              "wiki_context": {
                "mode": "page",
                "root_name": "Research",
                "page_title": "Plan"
              },
              "observations": [
                { "type": "wiki", "root_names": ["Research"] },
                { "type": "files", "paths": ["notes/input.txt"] }
              ]
            }
            """;

        var test = JsonSerializer.Deserialize<HarnessTestCase>(json);

        Assert.NotNull(test);
        Assert.Equal("once", test.PermissionDecision);
        Assert.Equal("Research", test.StateSetup.WikiRoots.Single().Name);
        Assert.Equal("notes/input.txt", test.StateSetup.Files[0].Path);
        Assert.Equal("docs/sample.bin", test.StateSetup.Files[1].Path);
        Assert.Equal("AAECAw==", test.StateSetup.Files[1].ContentBase64);
        Assert.Equal("Plan", test.StateSetup.WikiRoots.Single().Pages.Single().Title);
        Assert.Equal("page", test.WikiContext!.Mode);
        Assert.Equal("Research", test.WikiContext.RootName);
        Assert.Equal("Plan", test.WikiContext.PageTitle);
        Assert.Equal("wiki", test.Observations[0].Type);
        Assert.Equal("Research", test.Observations[0].RootNames.Single());
        Assert.Equal("notes/input.txt", test.Observations[1].Paths.Single());
    }

    [Fact]
    public void ResolveWikiObservationScope_EmptyNamesMeansObserveAllRoots()
    {
        HarnessObservationRequest[] requests =
        [
            new() { Type = "wiki", RootNames = [] },
            new() { Type = "files", Paths = ["notes/input.txt"] }
        ];

        var (observeWiki, rootNames) =
            HybridRuntimeHostAdapter.ResolveWikiObservationScope(requests);

        Assert.True(observeWiki);
        Assert.Empty(rootNames);
    }

    [Fact]
    public void ResolveWikiObservationScope_NoWikiRequestDoesNotObserveWiki()
    {
        HarnessObservationRequest[] requests =
        [
            new() { Type = "files", Paths = ["notes/input.txt"] }
        ];

        var (observeWiki, rootNames) =
            HybridRuntimeHostAdapter.ResolveWikiObservationScope(requests);

        Assert.False(observeWiki);
        Assert.Empty(rootNames);
    }

    [Fact]
    public void ResolveWikiObservationScope_NamedRootsRemainScoped()
    {
        HarnessObservationRequest[] requests =
        [
            new() { Type = "WIKI", RootNames = ["Research", "", "Research"] }
        ];

        var (observeWiki, rootNames) =
            HybridRuntimeHostAdapter.ResolveWikiObservationScope(requests);

        Assert.True(observeWiki);
        Assert.Equal(["Research"], rootNames);
    }

    [Theory]
    [InlineData("deny", "deny")]
    [InlineData(" ONCE ", "once")]
    [InlineData("always", "always")]
    [InlineData("unexpected", "session")]
    [InlineData(null, "session")]
    public void NormalizePermissionDecision_UsesSupportedDecisionOrSessionDefault(
        string? value,
        string expected)
    {
        Assert.Equal(expected, HybridRuntimeHostAdapter.NormalizePermissionDecision(value));
    }

    [Fact]
    public void ResolveUniqueNamedId_IsCaseInsensitiveAndFailsClosed()
    {
        using var document = JsonDocument.Parse("""
            [{"id":"root-1","name":"Research"},{"id":"root-2","name":"Other"}]
            """);

        Assert.Equal(
            "root-1",
            HybridRuntimeHostAdapter.ResolveUniqueNamedId(document.RootElement, "research", "Wiki root"));
        Assert.Throws<InvalidOperationException>(() =>
            HybridRuntimeHostAdapter.ResolveUniqueNamedId(document.RootElement, "Missing", "Wiki root"));
    }

    [Fact]
    public void ResolveHarnessFilePath_RejectsTraversalAndRootedPaths()
    {
        Directory.CreateDirectory(_root);

        Assert.Throws<InvalidOperationException>(() =>
            HybridRuntimeHostAdapter.ResolveHarnessFilePath(_root, "../escape.txt"));
        Assert.Throws<InvalidOperationException>(() =>
            HybridRuntimeHostAdapter.ResolveHarnessFilePath(_root, Path.Combine(_root, "absolute.txt")));
        Assert.StartsWith(
            Path.GetFullPath(_root),
            HybridRuntimeHostAdapter.ResolveHarnessFilePath(_root, "notes/input.txt"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HarnessFileContent_DecodesTextAndBinaryFixtures()
    {
        var text = HarnessFileContent.Decode(new HarnessFileSetup
        {
            Path = "notes/input.txt",
            Content = "local evidence"
        });
        var binary = HarnessFileContent.Decode(new HarnessFileSetup
        {
            Path = "docs/sample.bin",
            ContentBase64 = "AAECAw=="
        });

        Assert.Equal("local evidence", System.Text.Encoding.UTF8.GetString(text));
        Assert.Equal([0, 1, 2, 3], binary);
    }

    [Fact]
    public void HarnessFileContent_RejectsAmbiguousOrInvalidBinaryFixtures()
    {
        var ambiguous = new HarnessFileSetup
        {
            Path = "docs/sample.bin",
            Content = "text",
            ContentBase64 = "AAECAw=="
        };
        var invalid = new HarnessFileSetup
        {
            Path = "docs/sample.bin",
            ContentBase64 = "not base64"
        };

        Assert.Throws<InvalidOperationException>(() => HarnessFileContent.Decode(ambiguous));
        Assert.Throws<InvalidOperationException>(() => HarnessFileContent.Decode(invalid));
    }

    [Fact]
    public void ResolveHybridBuildProjects_IncludesRuntimeAndMcpServer()
    {
        var runtimeProject = Path.Combine(
            _root, "src", "Thaddeus.Runtime", "Thaddeus.Runtime.csproj");
        var mcpProject = Path.Combine(
            _root,
            "apps", "mcp-server", "SirThaddeus.McpServer", "SirThaddeus.McpServer.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeProject)!);
        Directory.CreateDirectory(Path.GetDirectoryName(mcpProject)!);
        File.WriteAllText(runtimeProject, "<Project />");
        File.WriteAllText(mcpProject, "<Project />");

        var projects = HybridRuntimeHostAdapter.ResolveHybridBuildProjects(_root);

        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, path => path.EndsWith(
            Path.Combine("src", "Thaddeus.Runtime", "Thaddeus.Runtime.csproj"),
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projects, path => path.EndsWith(
            Path.Combine(
                "apps", "mcp-server", "SirThaddeus.McpServer", "SirThaddeus.McpServer.csproj"),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnosticsReader_ExportsOnlyAllowlistedFieldsAndAggregatesTiming()
    {
        var logDirectory = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllLines(Path.Combine(logDirectory, "thaddeus-runtime-test.log"),
        [
            "routing.latency turnId=turn-1 stage=pipeline_start elapsedMs=10 durationMs=1 prompt=SECRET",
            "PIPELINE_STEP_TIMING turn_id=turn-1 step=ToolLoop outcome=ok elapsed_ms=20 tool_args=SECRET",
            "PROMPT_ASSEMBLY_TIMING turn_id=turn-1 messages=4 tools=2 forced_tool=false elapsed_ms=3 response=SECRET",
            "llm.request_completed turnId=turn-1 task=primary durationMs=50 model=SECRET",
            "PIPELINE_STEP_TIMING turn_id=turn-1 step=CompletionValidation outcome=ok elapsed_ms=4",
            "PIPELINE_TIMING turn_id=turn-1 outcome=ok elapsed_ms=80",
            "routing.latency turnId=turn-1 stage=first_ui_delta elapsedMs=45 durationMs=2",
            "routing.latency turnId=turn-1 stage=pipeline_complete elapsedMs=90 durationMs=1",
            "EXPERIMENT_ACTIVATION turn_id=turn-1 event=generalized_candidate decision=activated suite_id=SECRET",
            "PIPELINE_TIMING turn_id=another-turn outcome=ok elapsed_ms=999"
        ]);
        using var liveWriter = new FileStream(
            Path.Combine(logDirectory, "thaddeus-runtime-test.log"),
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        var diagnostics = HarnessRuntimeDiagnosticsReader.Read(
            _root,
            "turn-1",
            new HarnessTiming(1, 2, 3, 4));
        var json = JsonSerializer.Serialize(diagnostics);

        Assert.True(diagnostics.FullCompositionObserved);
        Assert.Equal(50, diagnostics.TimingsMs.ProviderTotal);
        Assert.Equal(45, diagnostics.TimingsMs.FirstVisibleContent);
        Assert.Equal(1, diagnostics.CallCounts.ProviderRequests);
        Assert.Contains(diagnostics.Events, item =>
            item.Name == "experiment.activation" && item.Stage == "generalized_candidate");
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactWriter_WritesCamelCaseDiagnosticsContract()
    {
        var writer = new HarnessArtifactWriter();
        var paths = writer.CreatePaths(_root, "run", "suite", "case", 1);
        var diagnostics = new HarnessRuntimeDiagnostics
        {
            TurnId = "turn-1",
            FullCompositionObserved = true,
            TimingsMs = new HarnessDiagnosticTimings { EndToEnd = 5 },
            CallCounts = new HarnessDiagnosticCallCounts { ProviderRequests = 1 }
        };

        await writer.WriteDiagnosticsAsync(paths, diagnostics, CancellationToken.None);

        using var document = JsonDocument.Parse(File.ReadAllText(paths.DiagnosticsJsonPath));
        Assert.True(document.RootElement.GetProperty("fullCompositionObserved").GetBoolean());
        Assert.Equal(5, document.RootElement.GetProperty("timingsMs").GetProperty("endToEnd").GetDouble());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
