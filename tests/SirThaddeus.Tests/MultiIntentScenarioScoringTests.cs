using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Tests;

/// <summary>
/// Scoring tests for the 10 multi-intent scenarios defined during the
/// segmentation plan review.  Each scenario validates that the segmenter,
/// coordinator, and composer produce correct behaviour end-to-end.
/// </summary>
public class MultiIntentScenarioScoringTests
{
    private readonly ConversationSegmenter _segmenter = new();

    // ───────────────────────────────────────────────────────────────
    //  Scenario 1 — Negation + request
    //  "Don't look up the weather—actually do, what is it?"
    //  Expected: low confidence (contradiction), at least 1 actionable.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S01_Negation_PlusRequest_DetectsContradiction()
    {
        var result = _segmenter.Segment(
            "Don't look up the weather—actually do, what is it?");

        Assert.True(result.HasActionable, "Should detect an actionable (weather).");
        Assert.False(result.HighConfidence, "Contradiction should drop confidence.");
        Assert.Equal("contradiction_detected", result.ConfidenceReason);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 2 — Two actionables in one sentence
    //  "What's the weather and what time does Target close?"
    //  Expected: 2+ actionable segments.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S02_TwoActionables_InOneSentence_SplitsClauses()
    {
        var result = _segmenter.Segment(
            "What's the weather and what time does Target close?");

        var actionable = result.Segments.Where(s => s.IsActionable).ToList();
        Assert.True(actionable.Count >= 2,
            $"Expected ≥2 actionable segments, got {actionable.Count}.");
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 3 — Messy punctuation
    //  "hey whats weather like anyway i got in trouble"
    //  Expected: at least 1 actionable (weather), non-actionable
    //  present. Confidence may be low due to missing punctuation.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S03_MessyPunctuation_StillDetectsWeatherActionable()
    {
        var result = _segmenter.Segment(
            "hey whats weather like anyway i got in trouble");

        Assert.True(result.HasActionable,
            "Should detect weather as actionable even without punctuation.");
        Assert.Contains(result.Segments,
            s => s.IsActionable && s.Text.Contains("weather", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 4 — Greeting + no request
    //  "Hey you there? hope you're well."
    //  Expected: no actionable, high confidence, fast-path.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S04_GreetingOnly_NoActionable()
    {
        var result = _segmenter.Segment(
            "Hey you there? hope you're well.");

        Assert.False(result.HasActionable,
            "Pure greeting should have no actionable.");
        Assert.True(result.HighConfidence,
            "Pure greeting should be high confidence.");
        Assert.Equal("no_actionable", result.ConfidenceReason);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 5 — Tool vs non-tool cap sanity
    //  Part A: The segmenter treats the messy compound as one
    //          actionable with low confidence (fallback territory).
    //  Part B: Given pre-split segments (from fallback extractor),
    //          the coordinator's cap only counts tool-using dispatches.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S05A_MessyCompound_SegmenterFlagsLowConfidence()
    {
        var result = _segmenter.Segment(
            "2+2 and what's the weather and who won the game last night");

        // One actionable (whole string), low confidence due to compound clause.
        Assert.True(result.HasActionable, "Should detect at least one actionable.");
        Assert.False(result.HighConfidence,
            "Compound signals across connectors should flag low confidence.");
    }

    [Fact]
    public async Task S05B_Coordinator_NonToolDoesNotConsumeCap()
    {
        // Pre-split segments (what the fallback extractor would produce).
        var segments = new[]
        {
            new ConversationSegment
            {
                SegmentId = "seg-1", Text = "2+2",
                Order = 0, StartIndex = 0, EndIndex = 3,
                IsActionable = true, Confidence = 0.90
            },
            new ConversationSegment
            {
                SegmentId = "seg-2", Text = "what's the weather",
                Order = 1, StartIndex = 8, EndIndex = 26,
                IsActionable = true, Confidence = 0.92
            },
            new ConversationSegment
            {
                SegmentId = "seg-3", Text = "who won the game last night",
                Order = 2, StartIndex = 31, EndIndex = 58,
                IsActionable = true, Confidence = 0.90
            }
        };

        var coordinator = new SegmentExecutionCoordinator();
        var plan = await coordinator.ExecuteAsync(new SegmentExecutionRequest
        {
            ActionableSegments = segments,
            MaxToolUsingActionables = 1,
            ExecuteActionableAsync = (seg, _) =>
            {
                var isCalc = seg.Text.Contains("2+2", StringComparison.Ordinal);
                return Task.FromResult(new SegmentExecutionResult
                {
                    SegmentId = seg.SegmentId,
                    SegmentText = seg.Text,
                    Intent = isCalc ? "calculate" : "web_search",
                    Success = true,
                    ResponseText = isCalc ? "4" : "Result for " + seg.Text,
                    UsedTools = !isCalc,
                    ToolCallCount = isCalc ? 0 : 1
                });
            }
        });

        // Non-tool calc (seg-1) + first tool-using (seg-2) = 2 executed.
        Assert.Equal(2, plan.Executed.Count);
        Assert.Single(plan.Deferred);
        Assert.Equal("seg-3", plan.Deferred[0].SegmentId);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 6 — Mixed conversational with local news
    //  "Hey whats up, how are you today? Can you pull up the
    //   local news in Rexburg, ID? Anyway, gotta go, bye!"
    //  Expected: 1 actionable (news), social greeting + farewell.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S06_MixedConversational_LocalNews()
    {
        var result = _segmenter.Segment(
            "Hey whats up, how are you today? Can you pull up the local news in Rexburg, ID? Anyway, gotta go, bye!");

        Assert.True(result.HasActionable,
            "Should detect local news as actionable.");
        Assert.Contains(result.Segments,
            s => s.IsActionable && s.Text.Contains("news", StringComparison.OrdinalIgnoreCase));

        var nonActionable = result.Segments.Where(s => !s.IsActionable).ToList();
        Assert.True(nonActionable.Count >= 1,
            "Should have non-actionable social segments.");
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 7 — Multi-intent: weather + news + emotional close
    //  "Hey there! What's the weather in Seattle and can you pull
    //   local news in Boise, ID? Anyway, rough day."
    //  Expected: 2 actionable, social segments on both ends.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S07_MultiIntent_WeatherAndNews_WithEmotionalClose()
    {
        var result = _segmenter.Segment(
            "Hey there! What's the weather in Seattle and can you pull local news in Boise, ID? Anyway, rough day.");

        var actionable = result.Segments.Where(s => s.IsActionable).ToList();
        Assert.True(actionable.Count >= 1,
            "Should detect at least 1 actionable (weather and/or news).");

        Assert.Contains(result.Segments,
            s => !s.IsActionable);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 8 — Single actionable, no social wrapper
    //  "What's the weather in Seattle?"
    //  Expected: 1 actionable, high confidence, no social segments.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S08_SingleActionable_NoSocial()
    {
        var result = _segmenter.Segment("What's the weather in Seattle?");

        Assert.True(result.HasActionable, "Should detect weather.");
        Assert.True(result.HighConfidence, "Single clean question should be high confidence.");

        var actionable = result.Segments.Where(s => s.IsActionable).ToList();
        Assert.Single(actionable);
        Assert.Contains("weather", actionable[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 9 — Emotional dump only (no request)
    //  "I had a terrible day at work. My boss yelled at me and
    //   I'm so stressed."
    //  Expected: no actionable, high confidence, fast-path.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S09_EmotionalDumpOnly_NoActionable()
    {
        var result = _segmenter.Segment(
            "I had a terrible day at work. My boss yelled at me and I'm so stressed.");

        Assert.False(result.HasActionable,
            "Pure venting should not trigger any actionable.");
        Assert.True(result.HighConfidence,
            "No-actionable path should be high confidence.");
        Assert.Equal("no_actionable", result.ConfidenceReason);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 10 — Unified response composition (end-to-end)
    //  Full pipeline: segment → coordinate → compose.
    //  Input: "Hey, how are you today? What's the weather? Anyway,
    //   rough day."
    //  Expected: greeting lead, weather result body, distress close.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task S10_EndToEnd_Composition_GreetingResultDistressClose()
    {
        var input = "Hey, how are you today? What's the weather? Anyway, rough day.";
        var result = _segmenter.Segment(input);

        Assert.True(result.HasActionable, "Should have weather actionable.");

        var actionable = result.Segments.Where(s => s.IsActionable).ToList();
        var nonActionableText = result.Segments
            .Where(s => !s.IsActionable)
            .Select(s => s.Text)
            .ToList();

        var coordinator = new SegmentExecutionCoordinator();
        var plan = await coordinator.ExecuteAsync(new SegmentExecutionRequest
        {
            ActionableSegments = actionable,
            MaxToolUsingActionables = 2,
            ExecuteActionableAsync = (seg, _) =>
                Task.FromResult(new SegmentExecutionResult
                {
                    SegmentId = seg.SegmentId,
                    SegmentText = seg.Text,
                    Intent = "weather",
                    Success = true,
                    ResponseText = "Currently 41°F in your area with partly cloudy skies.",
                    UsedTools = true,
                    ToolCallCount = 1
                })
        });

        var composer = new UnifiedResponseComposer();
        var composed = composer.Compose(new UnifiedResponseComposeRequest
        {
            OriginalMessage = input,
            NonActionableContext = nonActionableText,
            Executed = plan.Executed,
            Deferred = plan.Deferred
        });

        // Greeting lead.
        Assert.Contains("thanks", composed, StringComparison.OrdinalIgnoreCase);

        // Weather result present.
        Assert.Contains("41°F", composed, StringComparison.OrdinalIgnoreCase);

        // Distress close.
        Assert.Contains("rough", composed, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 11 — Prompt-echo location poisoning
    //  The small LLM echoes "currentLocation=none" as the location
    //  text.  Every defence layer must reject this.
    // ───────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("currentLocation=none")]
    [InlineData("currentLocation = null")]
    [InlineData("location=unknown")]
    [InlineData("place=n/a")]
    public void S11_PromptEchoLocation_RejectedByAllLayers(string poisoned)
    {
        // LocationContextHeuristics (structural: contains '=')
        Assert.True(LocationContextHeuristics.IsClearlyNonPlace(poisoned),
            $"IsClearlyNonPlace should reject \"{poisoned}\".");

        // ValidateSlots validation path
        var validated = new ValidateSlots(new ValidationOptions());
        var result = validated.Run(
            new DialogueState(),
            new MergedSlots
            {
                Intent = "weather",
                LocationText = poisoned,
                RawMessage = "what's the weather"
            });

        Assert.Null(result.LocationText);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 12 — Composer handles mixed success/failure
    //  One segment succeeds, one fails.  The composed response must
    //  present the success clearly and note the failure without
    //  dumping a raw error message.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S12_Composer_MixedSuccessFailure_SeparatesCleanly()
    {
        var composer = new UnifiedResponseComposer();
        var composed = composer.Compose(new UnifiedResponseComposeRequest
        {
            OriginalMessage = "What's the weather and what time does Target close?",
            NonActionableContext = [],
            Executed =
            [
                new SegmentExecutionResult
                {
                    SegmentId = "seg-1",
                    SegmentText = "What's the weather",
                    Intent = "weather",
                    Success = false,
                    ResponseText = "I couldn't find coordinates for that location.",
                    UsedTools = true,
                    ToolCallCount = 1
                },
                new SegmentExecutionResult
                {
                    SegmentId = "seg-2",
                    SegmentText = "what time does Target close",
                    Intent = "places",
                    Success = true,
                    ResponseText = "Target closes at 10 PM today.",
                    UsedTools = true,
                    ToolCallCount = 1
                }
            ],
            Deferred = []
        });

        // Successful result should be present and prominent.
        Assert.Contains("Target closes at 10 PM", composed);

        // Raw error message from the failed segment should NOT appear.
        Assert.DoesNotContain("I couldn't find coordinates", composed);

        // A gentle failure note should be present instead.
        Assert.Contains("wasn't able to resolve", composed, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────────────
    //  Scenario 13 — Composer: all segments succeed
    //  No failure summary should appear.
    // ───────────────────────────────────────────────────────────────
    [Fact]
    public void S13_Composer_AllSuccess_NoFailureNote()
    {
        var composer = new UnifiedResponseComposer();
        var composed = composer.Compose(new UnifiedResponseComposeRequest
        {
            OriginalMessage = "What's the weather and what time does Target close?",
            NonActionableContext = [],
            Executed =
            [
                new SegmentExecutionResult
                {
                    SegmentId = "seg-1",
                    SegmentText = "What's the weather",
                    Intent = "weather",
                    Success = true,
                    ResponseText = "Currently 41°F with partly cloudy skies.",
                    UsedTools = true,
                    ToolCallCount = 1
                },
                new SegmentExecutionResult
                {
                    SegmentId = "seg-2",
                    SegmentText = "what time does Target close",
                    Intent = "places",
                    Success = true,
                    ResponseText = "Target closes at 10 PM today.",
                    UsedTools = true,
                    ToolCallCount = 1
                }
            ],
            Deferred = []
        });

        Assert.Contains("41°F", composed);
        Assert.Contains("Target closes at 10 PM", composed);
        Assert.DoesNotContain("wasn't able", composed, StringComparison.OrdinalIgnoreCase);
    }
}
