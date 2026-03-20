using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Reporting;

/// <summary>
/// Produces a prettified box-drawing run summary for the harness console output.
/// </summary>
public static class SuiteReporter
{
    private const int BoxWidth = 62;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed record TestResult
    {
        public required string SuiteName { get; init; }
        public required string TestId { get; init; }
        public required string TestName { get; init; }
        public required ScoreCard Score { get; init; }
        public required double MinScore { get; init; }
        public required bool Passed { get; init; }
        public required int Attempts { get; init; }
        public string? ArtifactDirectory { get; init; }
    }

    public sealed record ReportContext
    {
        public required string RunId { get; init; }
        public string? ModelName { get; init; }
        public string? JudgeMode { get; init; }
        public string? ArtifactsRoot { get; init; }
        public double? DurationSeconds { get; init; }
    }

    public static void PrintSummary(
        IReadOnlyList<TestResult> results,
        ReportContext context)
    {
        if (results.Count == 0)
            return;

        var passCount = results.Count(r => r.Passed);
        var failCount = results.Count - passCount;
        var avgScore = results.Average(r => r.Score.FinalScore);
        var suiteNames = results.Select(r => r.SuiteName).Distinct().ToList();
        var suiteLabel = suiteNames.Count == 1 ? suiteNames[0] : $"{suiteNames.Count} suites";
        var passRate = results.Count > 0 ? (double)passCount / results.Count * 100 : 0;

        // ── Header box ──
        Console.WriteLine();
        WriteBoxTop();
        WriteBoxLine("SIR THADDEUS  ·  E2E Harness Report");
        WriteBoxLine($"Run: {context.RunId}  ·  Suite: {suiteLabel}");

        var modelLine = new StringBuilder();
        if (!string.IsNullOrEmpty(context.ModelName))
            modelLine.Append($"Model: {context.ModelName}");
        if (!string.IsNullOrEmpty(context.JudgeMode))
        {
            if (modelLine.Length > 0) modelLine.Append("  ·  ");
            modelLine.Append($"Judge: {context.JudgeMode}");
        }
        if (modelLine.Length > 0)
            WriteBoxLine(modelLine.ToString());

        WriteBoxBottom();
        Console.WriteLine();

        // ── Suite summary box ──
        WriteSectionTop("SUITE SUMMARY");
        Console.WriteLine("│".PadRight(BoxWidth + 1) + "│");

        var durationStr = context.DurationSeconds.HasValue
            ? $"{context.DurationSeconds.Value:F1}s"
            : "—";

        WriteBoxContent($"Tests Run    {results.Count,-8} Passed   {passCount,-8} Failed   {failCount}");
        WriteBoxContent($"Pass Rate    {passRate:F1}%{"",-4} Avg Score  {avgScore:F2}{"",-4} Duration  {durationStr}");
        WriteSectionBottom();
        Console.WriteLine();

        // ── Per-test results ──
        foreach (var test in results)
        {
            PrintTestLine(test);
        }

        // ── Failed test breakdowns ──
        var failed = results.Where(r => !r.Passed).ToList();
        if (failed.Count > 0)
        {
            Console.WriteLine();
            WriteSectionTop("SCORE BREAKDOWN — FAILED TESTS");
            WriteSectionDivider();

            foreach (var test in failed)
            {
                Console.WriteLine("│".PadRight(BoxWidth + 1) + "│");
                WriteBoxContent(test.TestId);
                WriteBoxContent(new string('┄', Math.Min(test.TestId.Length + 4, BoxWidth - 4)));
                PrintScoreBreakdown(test.Score);
            }

            Console.WriteLine("│".PadRight(BoxWidth + 1) + "│");
            WriteSectionBottom();
        }

        // ── Artifacts box ──
        if (!string.IsNullOrEmpty(context.ArtifactsRoot))
        {
            Console.WriteLine();
            WriteSectionTop("ARTIFACTS");
            WriteBoxContent($"📁 {context.ArtifactsRoot}");
            WriteBoxContent("   Each test folder contains:");
            WriteBoxContent("   input.json · steps.jsonl · final.txt · score.json");
            WriteSectionBottom();
        }

        // ── Footer quote ──
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  \"One measures the quality of service by the satisfaction of");
        Console.WriteLine("   the guest, not by the polish on the silverware.\"");
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void WriteJsonSummary(
        IReadOnlyList<TestResult> results,
        ReportContext context,
        string outputPath)
    {
        var passCount = results.Count(r => r.Passed);
        var avgScore = results.Count > 0 ? results.Average(r => r.Score.FinalScore) : 0;

        var payload = new
        {
            run_id = context.RunId,
            suite = string.Join(", ", results.Select(r => r.SuiteName).Distinct()),
            model = context.ModelName ?? "",
            judge_mode = context.JudgeMode ?? "none",
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            duration_seconds = context.DurationSeconds,
            tests_run = results.Count,
            tests_passed = passCount,
            tests_failed = results.Count - passCount,
            pass_rate = results.Count > 0 ? Math.Round((double)passCount / results.Count, 3) : 0,
            average_score = Math.Round(avgScore, 2),
            results = results.Select(r => new
            {
                test_id = r.TestId,
                suite = r.SuiteName,
                score = r.Score.FinalScore,
                passed = r.Passed,
                hard_pass = r.Score.HardPass,
                attempts = r.Attempts,
                breakdown = new
                {
                    keyword_penalty = r.Score.KeywordPenalty,
                    deflection_penalty = r.Score.DeflectionPenalty,
                    tool_incorporation_penalty = r.Score.ToolIncorporationPenalty,
                    assertion_density_penalty = r.Score.AssertionDensityPenalty,
                    personality_adjustment = r.Score.PersonalityAdjustment,
                    deflection_phrase_count = r.Score.DeflectionPhraseCount,
                    hedge_ratio = r.Score.HedgeRatio,
                    tool_tokens_incorporated = r.Score.ToolTokensIncorporated,
                    tool_tokens_available = r.Score.ToolTokensAvailable,
                    required_keywords_found = r.Score.RequiredKeywordsFound,
                    required_keywords_total = r.Score.RequiredKeywordsTotal
                },
                hard_failures = r.Score.HardFailures
            }).ToList()
        };

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    // ── Per-test line ──────────────────────────────────────────

    private static void PrintTestLine(TestResult test)
    {
        var icon = test.Passed ? "✅" : "❌";
        var scoreColor = test.Passed ? ConsoleColor.Green : ConsoleColor.Red;

        Console.Write("  ");
        Console.ForegroundColor = scoreColor;
        Console.Write($"{icon}  {test.Score.FinalScore,4:F1}");
        Console.ResetColor();
        Console.WriteLine($"   {test.TestId}");

        // Build summary lines (2-3 lines max)
        var summaryLines = BuildTestSummaryLines(test);
        foreach (var line in summaryLines)
        {
            if (line.StartsWith("⚡"))
            {
                Console.Write("            ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(line);
                Console.ResetColor();
            }
            else if (line.StartsWith("HARD FAIL"))
            {
                Console.Write("            ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(line);
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"            {line}");
                Console.ResetColor();
            }
        }

        Console.WriteLine();
    }

    private static List<string> BuildTestSummaryLines(TestResult test)
    {
        var lines = new List<string>();
        var sc = test.Score;

        // Hard assertion failures take priority
        if (!sc.HardPass && sc.HardFailures.Count > 0)
        {
            lines.Add($"HARD FAIL: {sc.HardFailures[0]}");
        }

        // Deflection
        if (sc.DeflectionPhraseCount > 0)
        {
            lines.Add($"Deflection detected: {sc.DeflectionPhraseCount} phrase(s), penalty {sc.DeflectionPenalty:F1}");
        }

        // Tool incorporation
        if (sc.ToolTokensAvailable > 0)
        {
            lines.Add($"Tool results: {sc.ToolTokensIncorporated}/{sc.ToolTokensAvailable} tokens incorporated");
        }

        // Keyword coverage for failures
        if (!test.Passed && sc.RequiredKeywordsTotal > 0 && sc.RequiredKeywordsFound < sc.RequiredKeywordsTotal)
        {
            lines.Add($"Keywords: {sc.RequiredKeywordsFound}/{sc.RequiredKeywordsTotal} required found (penalty {sc.KeywordPenalty:F1})");
        }

        // If nothing negative to say, give a positive note
        if (lines.Count == 0 && test.Passed)
        {
            if (sc.ToolTokensAvailable > 0 && sc.ToolIncorporationPenalty == 0)
                lines.Add($"Tool results well-incorporated ({sc.ToolTokensIncorporated}/{sc.ToolTokensAvailable} tokens).");
            else if (sc.RequiredKeywordsTotal > 0 && sc.RequiredKeywordsFound == sc.RequiredKeywordsTotal)
                lines.Add("All required keywords present. Good coverage.");
            else
                lines.Add("Response answered directly.");
        }

        // Minor warnings for passing tests
        if (test.Passed)
        {
            if (sc.HedgeRatio > 0.4)
                lines.Add($"⚡ Minor: hedging ratio {sc.HedgeRatio:F2} (borderline but acceptable)");
            if (sc.PersonalityAdjustment < -0.5)
                lines.Add($"⚡ Minor: personality adjustment {sc.PersonalityAdjustment:F1}");
        }

        // Arrow summary for failing tests
        if (!test.Passed && sc.DeflectionPhraseCount > 0 && sc.ToolTokensAvailable > 0 && sc.ToolIncorporationPenalty < -2)
            lines.Add("→ Agent searched but discarded all findings.");
        else if (!test.Passed && sc.DeflectionPhraseCount > 0)
            lines.Add("→ Agent deflected without providing a real answer.");

        return lines.Take(4).ToList();
    }

    // ── Score breakdown for failed tests ──────────────────────

    private static void PrintScoreBreakdown(ScoreCard sc)
    {
        WriteBreakdownRow("Base Score", "10.0", "");
        if (sc.RequiredKeywordsTotal > 0 || sc.KeywordPenalty != 0)
            WriteBreakdownRow("Keywords",
                $"{sc.KeywordPenalty:F1}",
                $"({sc.RequiredKeywordsFound}/{sc.RequiredKeywordsTotal} required keywords found)");
        if (sc.DeflectionPenalty != 0)
            WriteBreakdownRow("Deflection",
                $"{sc.DeflectionPenalty:F1}",
                $"({sc.DeflectionPhraseCount} deflection phrase(s))");
        if (sc.ToolIncorporationPenalty != 0 || sc.ToolTokensAvailable > 0)
            WriteBreakdownRow("Tool Incorp.",
                $"{sc.ToolIncorporationPenalty:F1}",
                $"({sc.ToolTokensIncorporated}/{sc.ToolTokensAvailable} result tokens incorporated)");
        if (sc.AssertionDensityPenalty != 0 || sc.HedgeRatio > 0.3)
            WriteBreakdownRow("Assert. Density",
                $"{sc.AssertionDensityPenalty:F1}",
                $"(hedge ratio {sc.HedgeRatio:F2})");
        if (sc.PersonalityAdjustment != 0)
            WriteBreakdownRow("Personality",
                $"{sc.PersonalityAdjustment:+0.0;-0.0;0.0}",
                "");

        var hardLabel = sc.HardPass ? "PASS" : "FAIL → forced to 0.0";
        WriteBreakdownRow("Hard Assertions", hardLabel, "");
        WriteBreakdownRow("Final Score",
            $"{sc.FinalScore:F1}",
            $"(min_score: applied externally)");
    }

    private static void WriteBreakdownRow(string label, string value, string detail)
    {
        var sb = new StringBuilder("│  ");
        sb.Append(label.PadRight(17));

        Console.Write("│  ");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(label.PadRight(17));
        Console.ResetColor();
        Console.Write(value.PadRight(8));
        if (!string.IsNullOrEmpty(detail))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(detail);
            Console.ResetColor();
        }
        // Pad to box width
        var contentLen = 2 + 17 + 8 + detail.Length;
        if (contentLen < BoxWidth)
            Console.Write(new string(' ', BoxWidth - contentLen));
        Console.WriteLine("│");
    }

    // ── Box-drawing helpers ───────────────────────────────────

    private static void WriteBoxTop()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write('╔');
        Console.Write(new string('═', BoxWidth));
        Console.WriteLine('╗');
        Console.ResetColor();
    }

    private static void WriteBoxBottom()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write('╚');
        Console.Write(new string('═', BoxWidth));
        Console.WriteLine('╝');
        Console.ResetColor();
    }

    private static void WriteBoxLine(string text)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("║  ");
        Console.ResetColor();
        var padded = text.Length > BoxWidth - 4
            ? text[..(BoxWidth - 4)]
            : text.PadRight(BoxWidth - 2);
        Console.Write(padded);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine('║');
        Console.ResetColor();
    }

    private static void WriteSectionTop(string title)
    {
        Console.Write('┌');
        Console.Write(new string('─', BoxWidth));
        Console.WriteLine('┐');
        WriteBoxContent(title);
    }

    private static void WriteSectionBottom()
    {
        Console.Write('└');
        Console.Write(new string('─', BoxWidth));
        Console.WriteLine('┘');
    }

    private static void WriteSectionDivider()
    {
        Console.Write('├');
        Console.Write(new string('─', BoxWidth));
        Console.WriteLine('┤');
    }

    private static void WriteBoxContent(string text)
    {
        var padded = text.Length > BoxWidth - 4
            ? text[..(BoxWidth - 4)]
            : text;
        var trailing = BoxWidth - 2 - padded.Length;
        Console.Write("│  ");
        Console.Write(padded);
        if (trailing > 0)
            Console.Write(new string(' ', trailing));
        Console.WriteLine('│');
    }
}
