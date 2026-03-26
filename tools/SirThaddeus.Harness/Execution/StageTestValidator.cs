using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Harness.Models;
using SirThaddeus.LlmClient;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Validates pipeline stage checkpoints for a collection of stage test cases.
/// Returns structured pass/fail results with diagnostics for each checkpoint.
/// </summary>
internal sealed class StageTestValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public sealed record StageCheckResult
    {
        public required string TestId { get; init; }
        public required string TestName { get; init; }
        public bool Passed { get; init; }
        public IReadOnlyList<string> Failures { get; init; } = [];
        public IReadOnlyList<string> Info { get; init; } = [];
        public long DurationMs { get; init; }
    }

    public async Task<IReadOnlyList<StageCheckResult>> ValidateAsync(
        IReadOnlyList<StageTestCase> tests,
        HarnessStageTarget target = HarnessStageTarget.All,
        CancellationToken cancellationToken = default)
    {
        var results = new List<StageCheckResult>(tests.Count);
        var classifyEnabled = target is HarnessStageTarget.All or HarnessStageTarget.Classify or HarnessStageTarget.Query;
        var defaultUserCity = TryLoadDefaultUserCity();

        // Set up shared infrastructure once
        var preprocessor = new RequestPreprocessor();
        IRequestClassifier? classifier = null;
        PipelineQueryBuilder? queryBuilder = null;

        if (classifyEnabled)
        {
            try
            {
                var settings = SirThaddeus.Config.SettingsManager.Load();
                var llm = new LmStudioClient(RuntimeLlmOptionsFactory.BuildPrimary(settings));
                var router = new DefaultRouter(llm, new DeterministicUtilityEngineAdapter());
                classifier = new RequestClassifier(router);
                queryBuilder = new PipelineQueryBuilder();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[stage-validator] Could not initialize classifier: {ex.Message}");
                Console.Error.WriteLine("[stage-validator] Classify and query checks will be skipped.");
            }
        }

        foreach (var test in tests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ValidateSingleAsync(test, target, defaultUserCity, preprocessor, classifier, queryBuilder, cancellationToken);
            results.Add(result);
        }

        return results;
    }

    private async Task<StageCheckResult> ValidateSingleAsync(
        StageTestCase test,
        HarnessStageTarget target,
        string defaultUserCity,
        IRequestPreprocessor preprocessor,
        IRequestClassifier? classifier,
        PipelineQueryBuilder? queryBuilder,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();
        var info = new List<string>();

        // ── Preprocess checks ────────────────────────────────────
        var preprocessed = preprocessor.Decompose(test.Input);
        info.Add($"Preprocessor: {preprocessed.Intents.Count} intent(s), multi={preprocessed.IsMultiIntent}");

        if (target is HarnessStageTarget.All or HarnessStageTarget.Preprocess && test.Checks.Preprocess is { } pc)
        {
            if (pc.ExpectedIntentCount is not null && preprocessed.Intents.Count != pc.ExpectedIntentCount)
            {
                failures.Add($"Preprocess: expected {pc.ExpectedIntentCount} intents, got {preprocessed.Intents.Count}");
            }

            if (pc.IsMultiIntent is not null && preprocessed.IsMultiIntent != pc.IsMultiIntent)
            {
                failures.Add($"Preprocess: expected IsMultiIntent={pc.IsMultiIntent}, got {preprocessed.IsMultiIntent}");
            }

            foreach (var must in pc.IntentMustContain)
            {
                if (!preprocessed.Intents.Any(i =>
                    i.NormalizedRequest.Contains(must, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"Preprocess: no intent contains \"{must}\"");
                }
            }

            foreach (var mustNot in pc.IntentMustNotContain)
            {
                if (preprocessed.Intents.Any(i =>
                    i.NormalizedRequest.Contains(mustNot, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"Preprocess: an intent contains forbidden text \"{mustNot}\"");
                }
            }
        }

        // ── Classify checks ──────────────────────────────────────
        ClassifierResult? classified = null;
        var shouldRunClassifier = target is HarnessStageTarget.All or HarnessStageTarget.Classify or HarnessStageTarget.Query;
        var needsClassification = shouldRunClassifier && (test.Checks.Classify is not null || test.Checks.Query is not null);

        if (needsClassification && classifier is not null)
        {
            var classifierContext = StageContextUtilities.BuildClassifierContext(test.Context);
            classified = await classifier.ClassifyAsync(preprocessed, classifierContext, cancellationToken);
            info.Add($"Classifier: {classified.ClassifiedIntents.Count} classified, deterministic={classified.AllDeterministic}");

            var cc = test.Checks.Classify;
            if (target is HarnessStageTarget.All or HarnessStageTarget.Classify && cc is not null)
            {
                foreach (var expected in cc.ExpectedIntents)
                {
                    if (!classified.ClassifiedIntents.Any(ci =>
                        string.Equals(ci.ResolvedIntent, expected, StringComparison.OrdinalIgnoreCase)))
                    {
                        failures.Add($"Classify: expected intent \"{expected}\" not found");
                    }
                }

                foreach (var forbidden in cc.ForbiddenIntents)
                {
                    if (classified.ClassifiedIntents.Any(ci =>
                        string.Equals(ci.ResolvedIntent, forbidden, StringComparison.OrdinalIgnoreCase)))
                    {
                        failures.Add($"Classify: forbidden intent \"{forbidden}\" was present");
                    }
                }

                if (cc.MustBeDeterministic == true && !classified.AllDeterministic)
                {
                    failures.Add("Classify: expected all-deterministic classification but had LLM-assisted routes");
                }
            }
        }
        else if ((target is HarnessStageTarget.All or HarnessStageTarget.Classify && test.Checks.Classify is not null) ||
                 (target == HarnessStageTarget.Query && test.Checks.Query is not null))
        {
            info.Add("Classify: skipped (no classifier available)");
        }

        // ── Query checks ─────────────────────────────────────────
        if (target is HarnessStageTarget.All or HarnessStageTarget.Query &&
            test.Checks.Query is not null &&
            classified is not null &&
            queryBuilder is not null)
        {
            var ctx = BuildDefaultContext(test.Context, defaultUserCity);
            var queryResult = await queryBuilder.BuildAsync(classified, ctx, cancellationToken);
            var allSearchQueries = queryResult.Queries
                .Where(q => !string.IsNullOrWhiteSpace(q.SearchQuery))
                .Select(q => q.SearchQuery)
                .ToList();

            info.Add($"QueryBuilder: {queryResult.Queries.Count} queries, {allSearchQueries.Count} search queries");

            var qc = test.Checks.Query;

            foreach (var must in qc.SearchQueryMustContain)
            {
                if (!allSearchQueries.Any(q => q.Contains(must, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"Query: no search query contains \"{must}\"");
                }
            }

            foreach (var mustNot in qc.SearchQueryMustNotContain)
            {
                if (allSearchQueries.Any(q => q.Contains(mustNot, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"Query: a search query contains forbidden text \"{mustNot}\"");
                }
            }

            if (qc.MaxQueryLength is not null)
            {
                foreach (var q in allSearchQueries.Where(q => q.Length > qc.MaxQueryLength))
                {
                    failures.Add($"Query: search query exceeds max length {qc.MaxQueryLength}: \"{q[..Math.Min(q.Length, 60)]}...\" ({q.Length} chars)");
                }
            }

            if (qc.MustHaveLocationContext == true)
            {
                if (!allSearchQueries.Any(q => q.Contains(" in ", StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add("Query: expected location context injection but no query contains 'in <location>'");
                }
            }
        }
        else if (target is HarnessStageTarget.All or HarnessStageTarget.Query && test.Checks.Query is not null)
        {
            info.Add("Query: skipped (no classifier/query builder available)");
        }

        sw.Stop();

        return new StageCheckResult
        {
            TestId = test.Id,
            TestName = test.Name,
            Passed = failures.Count == 0,
            Failures = failures,
            Info = info,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static QueryBuilderContext BuildDefaultContext(StageExecutionContext context, string defaultUserCity)
    {
        return StageContextUtilities.BuildQueryBuilderContext(context, defaultUserCity);
    }

    private static string TryLoadDefaultUserCity()
    {
        try
        {
            var settings = SirThaddeus.Config.SettingsManager.Load();
            return settings.GetEffectiveUserLocation().Value;
        }
        catch
        {
            return "";
        }
    }

    public static void PrintResults(IReadOnlyList<StageCheckResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("── STAGE VALIDATION RESULTS ────────────────────────");
        var passed = results.Count(r => r.Passed);
        var failed = results.Count - passed;

        foreach (var r in results)
        {
            var status = r.Passed ? "[PASS]" : "[FAIL]";
            Console.WriteLine($"  {status} {r.TestId} - {r.TestName} ({r.DurationMs}ms)");
            foreach (var info in r.Info)
                Console.WriteLine($"         {info}");
            foreach (var fail in r.Failures)
                Console.WriteLine($"         ✗ {fail}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Passed: {passed}  Failed: {failed}  Total: {results.Count}");
    }
}
