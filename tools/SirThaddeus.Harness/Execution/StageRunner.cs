using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Harness.Cli;
using SirThaddeus.LlmClient;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Runs pipeline stages independently and prints structured diagnostic output.
/// This enables measuring each stage's behavior without running a full E2E test.
///
/// Usage:
///   harness stage preprocess --input "Hey! What's the weather in Seattle?"
///   harness stage preflight --input "tell me more" --assistant-context "Here are 10 bakeries I found nearby..."
///   harness stage classify --input "What's the latest news about AI?"
///   harness stage query --input "Search for restaurants near me"
///   harness stage trace --input "Hey! Summarize today's news and then draft an email"
/// </summary>
internal sealed class StageRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<int> RunAsync(HarnessCommandOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.StageInput))
        {
            return await RunAdHocAsync(options, cancellationToken);
        }

        // Suite-based stage checking (Phase 2B integration point)
        Console.WriteLine("Suite-based stage testing not yet implemented. Use --input for ad-hoc stage analysis.");
        return 1;
    }

    private async Task<int> RunAdHocAsync(HarnessCommandOptions options, CancellationToken cancellationToken)
    {
        var input = options.StageInput;
        var target = options.StageTarget;
        var preflightContext = CreateClassifierContext(options);

        Console.WriteLine($"Stage command: {target}");
        Console.WriteLine($"Input: \"{input}\"");
        if (!string.IsNullOrWhiteSpace(options.StageAssistantContext))
            Console.WriteLine($"Assistant context: \"{Truncate(options.StageAssistantContext, 120)}\"");
        if (options.StageHasRecentSearchResults || options.StageHasRecentFirstPrinciplesRationale)
        {
            Console.WriteLine(
                $"Context flags: recent_search={options.StageHasRecentSearchResults}, recent_rationale={options.StageHasRecentFirstPrinciplesRationale}");
        }
        Console.WriteLine();

        var preprocessor = new RequestPreprocessor();
        var trace = new StageTraceEmitter();
        AppSettingsSnapshot settings = LoadSettingsSnapshot(options);

        // ── Stage 1: Preprocess ──────────────────────────────────
        var sw = Stopwatch.StartNew();
        var preprocessed = preprocessor.Decompose(input);
        sw.Stop();
        trace.RecordPreprocess(preprocessed, sw.ElapsedMilliseconds);

        PrintStageHeader("PREPROCESS");
        PrintPreprocessorResult(preprocessed, sw.ElapsedMilliseconds);

        if (target == HarnessStageTarget.Preprocess)
        {
            PrintTraceFooter(trace, input);
            return 0;
        }

        // ── Stage 2: Classify ────────────────────────────────────
        // The classifier needs a router. Try to get one from the runtime,
        // or fall back to displaying what we know deterministically.
        ClassifierResult? classified = null;
        PreflightReport? preflight = null;
        long classifyMs = 0;

        try
        {
            var llm = new LmStudioClient(RuntimeLlmOptionsFactory.BuildPrimary(settings.Settings));
            var router = new DefaultRouter(llm, new DeterministicUtilityEngineAdapter());
            var classifier = new RequestClassifier(router);

            if (target == HarnessStageTarget.Preflight)
            {
                sw.Restart();
                preflight = await BuildPreflightReportAsync(options, input, router, llm, cancellationToken);
                sw.Stop();

                PrintStageHeader("PREFLIGHT");
                PrintPreflightReport(preflight, sw.ElapsedMilliseconds);
            }

            sw.Restart();
            classified = await classifier.ClassifyAsync(preprocessed, preflightContext, cancellationToken);
            sw.Stop();
            classifyMs = sw.ElapsedMilliseconds;
            trace.RecordClassify(classified, classifyMs);

            PrintStageHeader("CLASSIFY");
            PrintClassifierResult(classified, classifyMs);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[CLASSIFY] Skipped — router not available: {ex.Message}");
            Console.WriteLine("[CLASSIFY] Showing deterministic intent-type mapping only:");
            PrintDeterministicClassification(preprocessed);
        }

        if (target == HarnessStageTarget.Classify)
        {
            PrintTraceFooter(trace, input);
            return 0;
        }

        // ── Stage 3: Query Build ─────────────────────────────────
        QueryBuilderResult? queryResult = null;
        if (classified is not null)
        {
            var context = BuildDefaultContext(options, settings);
            var queryBuilder = new PipelineQueryBuilder();

            sw.Restart();
            queryResult = await queryBuilder.BuildAsync(classified, context, cancellationToken);
            sw.Stop();
            trace.RecordQueryBuild(queryResult, sw.ElapsedMilliseconds);

            PrintStageHeader("QUERY BUILD");
            PrintQueryBuilderResult(queryResult, sw.ElapsedMilliseconds);

            if (target == HarnessStageTarget.Preflight && preflight is not null)
            {
                preflight = preflight with
                {
                    QueryPreviews = queryResult.Queries
                        .Select(q => !string.IsNullOrWhiteSpace(q.SearchQuery)
                            ? q.SearchQuery
                            : q.PlannedTools.Count > 0
                                ? $"tools:{string.Join(",", q.PlannedTools.Select(t => t.ToolName))}"
                                : q.RequiresExecution
                                    ? "deferred"
                                    : q.InlineAnswer)
                        .ToList(),
                    Warnings = MergeWarnings(preflight.Warnings, BuildQueryWarnings(queryResult, options))
                };

                PrintPreflightWarnings(preflight.Warnings);
            }
        }

        if (target is HarnessStageTarget.Query or HarnessStageTarget.Preflight)
        {
            PrintTraceFooter(trace, input);
            await WriteStageArtifactsAsync(options, trace, input, preflight, cancellationToken);
            return 0;
        }

        // ── Full Trace ───────────────────────────────────────────
        PrintTraceFooter(trace, input);

        await WriteStageArtifactsAsync(options, trace, input, preflight, cancellationToken);

        return 0;
    }

    // ────────────────────────────────────────────────────────────────
    // Pretty-print helpers
    // ────────────────────────────────────────────────────────────────

    private static void PrintStageHeader(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"── {name} ──────────────────────────────────────");
    }

    private static void PrintPreflightReport(PreflightReport report, long ms)
    {
        Console.WriteLine($"  Duration: {ms}ms");
        Console.WriteLine($"  Signals: {report.Features.ToPromptSummary()}");
        Console.WriteLine(
            $"  Follow-up signals: search_follow_up={report.IsSearchFollowUp}, referential={report.IsReferential}, recent_search={report.HasRecentSearchResults}");

        if (!string.IsNullOrWhiteSpace(report.ResolvedTopicPreview))
            Console.WriteLine($"  Resolved topic preview: \"{report.ResolvedTopicPreview}\"");

        if (report.PrimaryRoute is not null)
        {
            Console.WriteLine(
                $"  Primary route: intent={report.PrimaryRoute.Intent} conf={report.PrimaryRoute.Confidence:0.00} risk={report.PrimaryRoute.RiskLevel}");
        }

        if (report.PrimaryPolicy is not null)
        {
            Console.WriteLine(
                $"  Policy: useToolLoop={report.PrimaryPolicy.UseToolLoop} capabilities=[{string.Join(",", report.PrimaryPolicy.AllowedCapabilities)}] perms=[{string.Join(",", report.PrimaryPolicy.RequiredPermissions)}]");
        }

        if (report.FootmanDecision is not null)
        {
            Console.WriteLine(
                $"  Footman preview: state={report.FootmanDecision.NextState} policy={report.FootmanDecision.EffectiveContextPolicy} conf={report.FootmanDecision.Confidence:0.00} authoritative={report.FootmanDecision.IsAuthoritative} reason={report.FootmanDecision.ReasonCode}");
        }

        if (report.QueryPreviews.Count > 0)
            Console.WriteLine($"  Query previews: [{string.Join(" | ", report.QueryPreviews.Select(q => Truncate(q, 80)))}]");
    }

    private static void PrintPreflightWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
            return;

        Console.WriteLine("  Warnings:");
        foreach (var warning in warnings)
            Console.WriteLine($"    - {warning}");
    }

    private static void PrintPreprocessorResult(PreprocessorResult result, long ms)
    {
        Console.WriteLine($"  Intents: {result.Intents.Count} ({(result.IsMultiIntent ? "multi" : "single")})");
        Console.WriteLine($"  Duration: {ms}ms");
        foreach (var intent in result.Intents)
        {
            Console.WriteLine($"  [{intent.Order}] \"{intent.NormalizedRequest}\"");
            Console.WriteLine($"       original: \"{intent.OriginalFragment}\"");
            Console.WriteLine($"       confidence: {intent.Confidence:0.00}");
        }
    }

    private static void PrintClassifierResult(ClassifierResult result, long ms)
    {
        Console.WriteLine($"  Classified: {result.ClassifiedIntents.Count} intent(s)");
        Console.WriteLine($"  All deterministic: {result.AllDeterministic}");
        Console.WriteLine($"  Duration: {ms}ms");
        foreach (var ci in result.ClassifiedIntents)
        {
            Console.WriteLine($"  [{ci.Source.Order}] intent={ci.ResolvedIntent} type={ci.MappedType} conf={ci.Confidence:0.00}");
            if (ci.RouterOutput is not null)
            {
                var ro = ci.RouterOutput;
                var flags = new List<string>();
                if (ro.NeedsWeb) flags.Add("web");
                if (ro.NeedsSearch) flags.Add("search");
                if (ro.NeedsFileAccess) flags.Add("file");
                if (ro.NeedsMemoryRead) flags.Add("memRead");
                if (ro.NeedsMemoryWrite) flags.Add("memWrite");
                if (ro.NeedsScreenRead) flags.Add("screen");
                if (ro.NeedsSystemExecute) flags.Add("system");
                if (flags.Count > 0)
                    Console.WriteLine($"       needs: [{string.Join(", ", flags)}]");
                Console.WriteLine($"       risk: {ro.RiskLevel}");
            }
            if (ci.Policy is not null)
            {
                Console.WriteLine($"       policy: useToolLoop={ci.Policy.UseToolLoop} allowed=[{string.Join(",", ci.Policy.AllowedTools.Take(5))}]");
            }
        }
    }

    private static void PrintDeterministicClassification(PreprocessorResult preprocessed)
    {
        foreach (var intent in preprocessed.Intents)
        {
            var lower = intent.NormalizedRequest.ToLowerInvariant();
            var guessType = lower switch
            {
                _ when lower.Contains("weather") || lower.Contains("news") || lower.Contains("search") => "WebSearch",
                _ when lower.Contains("file") || lower.Contains("read") => "FileRead",
                _ when lower.Contains("write") || lower.Contains("create") => "FileWrite",
                _ when lower.Contains("run") || lower.Contains("execute") => "CodeExecution",
                _ => "Chat/Unknown"
            };
            Console.WriteLine($"  [{intent.Order}] \"{intent.NormalizedRequest}\" → {guessType} (heuristic)");
        }
    }

    private static void PrintQueryBuilderResult(QueryBuilderResult result, long ms)
    {
        Console.WriteLine($"  Queries: {result.Queries.Count}");
        Console.WriteLine($"  Duration: {ms}ms");
        foreach (var q in result.Queries)
        {
            if (!string.IsNullOrWhiteSpace(q.SearchQuery))
                Console.WriteLine($"  search: \"{q.SearchQuery}\"");
            else if (q.PlannedTools.Count > 0)
                Console.WriteLine($"  tools: [{string.Join(", ", q.PlannedTools.Select(t => t.ToolName))}]");
            else if (!string.IsNullOrWhiteSpace(q.InlineAnswer))
                Console.WriteLine($"  inline: \"{Truncate(q.InlineAnswer, 80)}\"");
            else
                Console.WriteLine($"  deferred: requires execution");

            Console.WriteLine($"       requires_execution: {q.RequiresExecution}");
        }
    }

    private static void PrintTraceFooter(StageTraceEmitter trace, string input)
    {
        Console.WriteLine();
        Console.WriteLine("── STAGE TRACE SUMMARY ────────────────────────────");
        var fullTrace = trace.BuildTrace(input);
        Console.WriteLine($"  Correlation: {fullTrace.CorrelationId}");
        Console.WriteLine($"  Total: {fullTrace.TotalDurationMs}ms across {fullTrace.Stages.Count} stage(s)");
        foreach (var stage in fullTrace.Stages)
        {
            var warnTag = stage.Warnings.Count > 0 ? $" ⚠{stage.Warnings.Count}" : "";
            Console.WriteLine($"  {stage.Stage,-12} {stage.DurationMs,6}ms  {stage.Decision,-20} {stage.OutputSummary}{warnTag}");
        }
    }

    private static QueryBuilderContext BuildDefaultContext(HarnessCommandOptions options, AppSettingsSnapshot settings)
    {
        var userCity = !string.IsNullOrWhiteSpace(options.StageUserCity)
            ? options.StageUserCity
            : settings.UserCity;

        var recentMessages = new List<(string Role, string Content)>();
        if (!string.IsNullOrWhiteSpace(options.StageAssistantContext))
            recentMessages.Add(("assistant", options.StageAssistantContext));

        return new QueryBuilderContext
        {
            UserCity = userCity,
            UserTimezone = TimeZoneInfo.Local.Id,
            RecentMessages = recentMessages
        };
    }

    private static ClassifierContext CreateClassifierContext(HarnessCommandOptions options)
    {
        return new ClassifierContext
        {
            HasRecentFirstPrinciplesRationale = options.StageHasRecentFirstPrinciplesRationale,
            HasRecentSearchResults = options.StageHasRecentSearchResults
        };
    }

    private static AppSettingsSnapshot LoadSettingsSnapshot(HarnessCommandOptions options)
    {
        try
        {
            var settings = SirThaddeus.Config.SettingsManager.Load();
            var location = settings.GetEffectiveUserLocation();
            return new AppSettingsSnapshot(settings, !string.IsNullOrWhiteSpace(options.StageUserCity) ? options.StageUserCity : location.Value);
        }
        catch
        {
            return new AppSettingsSnapshot(new SirThaddeus.Config.AppSettings(), options.StageUserCity);
        }
    }

    private static async Task<PreflightReport> BuildPreflightReportAsync(
        HarnessCommandOptions options,
        string input,
        IRouter router,
        ILlmClient llm,
        CancellationToken cancellationToken)
    {
        var features = RoutingFeatures.Extract(
            input,
            hasRecentRationale: options.StageHasRecentFirstPrinciplesRationale,
            hasRecentSearchResults: options.StageHasRecentSearchResults);

        var routeRequest = new RouterRequest
        {
            UserMessage = input,
            HasRecentFirstPrinciplesRationale = options.StageHasRecentFirstPrinciplesRationale,
            HasRecentSearchResults = options.StageHasRecentSearchResults
        };

        var primaryRoute = await router.RouteAsync(routeRequest, cancellationToken);
        var policy = PolicyGate.Evaluate(primaryRoute);
        var footman = new FastLlmFootmanRouter(llm);
        var footmanDecision = await footman.RouteAsync(input, features, cancellationToken);
        var lowerInput = input.Trim().ToLowerInvariant();
        var isFollowUp = SearchModeRouter.IsFollowUpMessage(lowerInput);
        var isReferential = SearchModeRouter.IsReferential(lowerInput);

        var warnings = new List<string>();
        if ((isFollowUp || isReferential) && !options.StageHasRecentSearchResults)
            warnings.Add("Follow-up language detected without recent search results; production routing may treat this as a fresh query.");
        if ((isFollowUp || isReferential) && string.IsNullOrWhiteSpace(options.StageAssistantContext))
            warnings.Add("Follow-up language detected without assistant context; query resolution may drift or stay too vague.");

        var resolvedTopic = TryExtractAssistantTopic(options.StageAssistantContext);
        if (LooksLikeVagueFollowUp(input) && string.IsNullOrWhiteSpace(resolvedTopic))
            warnings.Add("The prompt is a vague follow-up and no concrete topic could be extracted from assistant context.");

        return new PreflightReport
        {
            Features = features,
            PrimaryRoute = primaryRoute,
            PrimaryPolicy = policy,
            FootmanDecision = footmanDecision,
            IsSearchFollowUp = isFollowUp,
            IsReferential = isReferential,
            HasRecentSearchResults = options.StageHasRecentSearchResults,
            ResolvedTopicPreview = resolvedTopic,
            Warnings = warnings
        };
    }

    private static IReadOnlyList<string> BuildQueryWarnings(QueryBuilderResult queryResult, HarnessCommandOptions options)
    {
        var warnings = new List<string>();
        foreach (var query in queryResult.Queries)
        {
            if (!string.IsNullOrWhiteSpace(query.SearchQuery) && LooksLikeVagueFollowUp(query.SearchQuery))
            {
                warnings.Add(
                    "Query builder still produced a vague follow-up search term; inspect follow-up resolution before trusting the answer.");
                break;
            }
        }

        if (warnings.Count == 0 && !string.IsNullOrWhiteSpace(options.StageAssistantContext))
        {
            var topicPreview = TryExtractAssistantTopic(options.StageAssistantContext);
            if (!string.IsNullOrWhiteSpace(topicPreview) &&
                queryResult.Queries.All(q => string.IsNullOrWhiteSpace(q.SearchQuery) || !q.SearchQuery.Contains(topicPreview, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add("Assistant context produced a concrete topic preview, but the built search query did not clearly reuse it.");
            }
        }

        return warnings;
    }

    private static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        return first.Concat(second).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string TryExtractAssistantTopic(string assistantContext)
    {
        if (string.IsNullOrWhiteSpace(assistantContext))
            return "";

        var cleaned = assistantContext.Trim();
        foreach (var leadIn in new[] { "bottom line:", "here's what i found:", "here is what i found:", "summary:", "in short:", "tl;dr:" })
        {
            if (cleaned.StartsWith(leadIn, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[leadIn.Length..].TrimStart();
                break;
            }
        }

        var sentenceEnd = cleaned.IndexOfAny(['.', '!', '?', '\n']);
        if (sentenceEnd > 10)
            cleaned = cleaned[..sentenceEnd].Trim();

        if (cleaned.Length > 80)
            cleaned = cleaned[..80].Trim();

        return cleaned.Length >= 5 ? cleaned : "";
    }

    private static bool LooksLikeVagueFollowUp(string query)
    {
        var normalized = (query ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        foreach (var pattern in new[]
                 {
                     "more info", "more information", "more on that", "more about that", "more about it",
                     "more on it", "more details", "tell me more", "go deeper", "elaborate", "that topic",
                     "the topic", "that story", "the story", "that article", "that", "it", "this"
                 })
        {
            if (normalized == pattern || normalized.Contains(pattern, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static async Task WriteStageArtifactsAsync(
        HarnessCommandOptions options,
        StageTraceEmitter trace,
        string input,
        PreflightReport? preflight,
        CancellationToken cancellationToken)
    {
        var artifactsRoot = options.ArtifactsRoot;
        if (string.IsNullOrWhiteSpace(artifactsRoot))
            return;

        var stageDir = Path.Combine(
            Path.IsPathRooted(artifactsRoot) ? artifactsRoot : Path.GetFullPath(artifactsRoot),
            "stage",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(stageDir);

        var tracePath = Path.Combine(stageDir, "stage_trace.json");
        await File.WriteAllTextAsync(
            tracePath,
            JsonSerializer.Serialize(trace.BuildTrace(input), JsonOptions),
            cancellationToken);

        var stagesPath = Path.Combine(stageDir, "stages.jsonl");
        await File.WriteAllTextAsync(stagesPath, trace.ToJsonLines(), cancellationToken);

        if (preflight is not null)
        {
            var preflightPath = Path.Combine(stageDir, "preflight_report.json");
            await File.WriteAllTextAsync(
                preflightPath,
                JsonSerializer.Serialize(preflight, JsonOptions),
                cancellationToken);
        }

        Console.WriteLine($"Artifacts: {stageDir}");
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text[..(max - 3)] + "...";
    }

    private sealed record AppSettingsSnapshot(SirThaddeus.Config.AppSettings Settings, string UserCity);

    private sealed record PreflightReport
    {
        public required RoutingFeatures Features { get; init; }
        public RouterOutput? PrimaryRoute { get; init; }
        public PolicyDecision? PrimaryPolicy { get; init; }
        public RoutingDecision? FootmanDecision { get; init; }
        public bool IsSearchFollowUp { get; init; }
        public bool IsReferential { get; init; }
        public bool HasRecentSearchResults { get; init; }
        public string ResolvedTopicPreview { get; init; } = "";
        public IReadOnlyList<string> QueryPreviews { get; init; } = [];
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }
}
