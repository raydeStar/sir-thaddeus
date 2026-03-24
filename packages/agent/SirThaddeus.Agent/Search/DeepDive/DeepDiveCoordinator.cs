using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// Coordinates provider calls, normalization, and final payload assembly
/// for deep-dive briefings.
/// </summary>
public sealed partial class DeepDiveCoordinator
{
    private const string PlacesLookupTool = "places_lookup";
    private const string PlacesLookupToolAlt = "PlacesLookup";
    private const string WebSearchTool = "web_search";
    private const string WebSearchToolAlt = "WebSearch";
    private const string BrowseTool = "browser_navigate";
    private const string BrowseToolAlt = "BrowserNavigate";
    private const int DefaultMaxToolCalls = 8;
    private const int DefaultMaxOpenedSources = 5;

    private readonly IMcpToolClient _mcp;
    private readonly IAuditLogger _audit;
    private readonly DeepDiveBriefingAssembler _assembler;

    public DeepDiveCoordinator(
        IMcpToolClient mcp,
        IAuditLogger audit,
        DeepDiveBriefingAssembler? assembler = null)
    {
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _assembler = assembler ?? new DeepDiveBriefingAssembler();
    }

    public async Task<DeepDiveExecutionResult> BuildPlaceBriefingAsync(
        string query,
        string timezone,
        string locale,
        string? userLocationHint,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var auditSteps = new List<DeepDiveAuditStep>();
        var sourceRefs = new List<SourceRef>();
        var warnings = new List<string>();
        var toolCallCount = 0;
        var maxToolCalls = ParseIntEnv("ST_DEEPDIVE_MAX_TOOL_CALLS", DefaultMaxToolCalls, min: 1, max: 20);
        var maxOpenedSources = ParseIntEnv("ST_DEEPDIVE_MAX_SOURCES", DefaultMaxOpenedSources, min: 1, max: 10);
        query = StripEmbeddedInstructionScaffold(query);
        var cleanedQuery = CleanQueryForWebFallback(query);
        var effectiveLocationHint = ResolveLocationHintForQuery(query, cleanedQuery, userLocationHint);

        AddAuditStep(auditSteps, "search", "Starting deep dive place lookup.");

        if (string.IsNullOrWhiteSpace(timezone))
        {
            timezone = "unknown";
            warnings.Add("Timezone is unknown. Hours may shift around daylight-savings boundaries.");
        }

        if (string.IsNullOrWhiteSpace(locale))
        {
            var localeFromEnv = Environment.GetEnvironmentVariable("ST_DEEPDIVE_DEFAULT_LOCALE");
            locale = string.IsNullOrWhiteSpace(localeFromEnv) ? "en-US" : localeFromEnv;
        }

        // Budget-aware tool invocation helper.
        async Task<string?> CallToolBoundedAsync(
            string primaryName,
            string alternateName,
            string argumentsJson,
            string auditStep,
            string auditDetail)
        {
            if (toolCallCount >= maxToolCalls)
            {
                warnings.Add("Tool budget reached before all data could be gathered.");
                AddAuditStep(auditSteps, auditStep, "Skipped because tool budget was exhausted.");
                return null;
            }

            toolCallCount++;
            string toolName = primaryName;
            string? output = null;
            var ok = false;

            try
            {
                output = await _mcp.CallToolAsync(primaryName, argumentsJson, cancellationToken);
                ok = output is null || !output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception firstEx)
            {
                try
                {
                    toolName = alternateName;
                    output = await _mcp.CallToolAsync(alternateName, argumentsJson, cancellationToken);
                    ok = output is null || !output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception secondEx)
                {
                    output = $"Tool error: {firstEx.Message}; fallback error: {secondEx.Message}";
                }
            }

            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName = toolName,
                Arguments = argumentsJson,
                Result = Truncate(output ?? "", 800),
                Success = ok
            });

            AddAuditStep(
                auditSteps,
                auditStep,
                $"{auditDetail} ({toolName}, success={ok.ToString().ToLowerInvariant()})");

            return output;
        }

        // 1) Places-first provider path.
        var placesArgs = JsonSerializer.Serialize(new
        {
            query,
            timezone,
            locale,
            userLocationHint = effectiveLocationHint,
            maxReviewSnippets = 3
        });

        var placesJson = await CallToolBoundedAsync(
            PlacesLookupTool,
            PlacesLookupToolAlt,
            placesArgs,
            "details_fetch",
            "Requested place details.");

        if (TryBuildFromPlacesPayload(
            query,
            timezone,
            locale,
            effectiveLocationHint,
            placesJson,
            sourceRefs,
            warnings,
            auditSteps,
            out var placesBriefing))
        {
            var result = ValidateAndFinalize(placesBriefing, warnings, sourceRefs, auditSteps);
            return result with
            {
                AssistantText = BuildAssistantLead(result.Briefing!)
            };
        }

        // 2) Explicit fallback path through web tools.
        warnings.Add("Places provider unavailable or incomplete. Fell back to web extraction.");
        AddAuditStep(auditSteps, "search", "Fallback path activated: web_search + browser_navigate.");

        // Strip conversational filler before building a search query.
        // "Can you tell me what the operating hours of Trader Joe's in Portland is?"
        // becomes "Trader Joe's Portland" - the kind of query that actually works.
        //
        // For web search (unlike Places API), always inject location context when
        // no explicit city is in the query.  Search engines handle proximity
        // gracefully - "Target near Rexburg, ID hours" is better than "Target hours".
        var webLocationSuffix = !string.IsNullOrWhiteSpace(effectiveLocationHint)
            && !cleanedQuery.Contains(effectiveLocationHint, StringComparison.OrdinalIgnoreCase)
            ? $" near {effectiveLocationHint}"
            : "";
        var webArgs = JsonSerializer.Serialize(new
        {
            query = $"{cleanedQuery}{webLocationSuffix} hours address phone",
            maxResults = 5,
            recency = "any"
        });

        var webResult = await CallToolBoundedAsync(
            WebSearchTool,
            WebSearchToolAlt,
            webArgs,
            "search",
            "Ran web search fallback.");

        var explicitRegionTokens = ExtractExplicitRegionTokens(cleanedQuery);
        var rawSources = Search.SearchOrchestrator.ParseSourcesFromToolResult(webResult ?? "")
            .Where(IsNavigablePlaceFallbackSource)
            .ToList();

        var sources = rawSources
            .Where(source => IsUsefulPlaceFallbackSource(source, cleanedQuery, explicitRegionTokens))
            .Take(maxOpenedSources)
            .ToList();

        if (sources.Count == 0 && explicitRegionTokens.Count == 0 && rawSources.Count > 0)
        {
            AddAuditStep(auditSteps, "search", "Strict fallback source filter removed all candidates; retrying with broader non-junk web results.");
            sources = rawSources.Take(maxOpenedSources).ToList();
        }

        if (sources.Count == 0)
            warnings.Add("Fallback search came back with 0 results for the query.");

        foreach (var source in sources)
        {
            sourceRefs.Add(new SourceRef
            {
                Name = string.IsNullOrWhiteSpace(source.Title) ? source.Domain : source.Title,
                Url = source.Url,
                FetchedIso = now.ToString("O")
            });
        }

        var extractedChunks = new List<string>();

        // Do not browse a generic search engine results page when web_search returned
        // no sources. Those pages are usually junk for synthesis and create noisy tool
        // traces with no real grounding value.
        if (sources.Count == 0)
            AddAuditStep(auditSteps, "search", "web_search returned 0 results - skipping generic search-engine browse fallback.");

        foreach (var source in sources.Take(2))
        {
            var args = JsonSerializer.Serialize(new { url = source.Url });
            var content = await CallToolBoundedAsync(
                BrowseTool,
                BrowseToolAlt,
                args,
                "open_page",
                $"Opened fallback page: {source.Url}");
            if (!string.IsNullOrWhiteSpace(content) && !content.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) && !content.StartsWith("Tool error:", StringComparison.OrdinalIgnoreCase))
                extractedChunks.Add(content!);
        }

        if (!string.IsNullOrWhiteSpace(webResult) && !webResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) && !webResult.StartsWith("Tool error:", StringComparison.OrdinalIgnoreCase))
            extractedChunks.Add(webResult!);

        // Source snippets often contain hours, address, phone, and ratings
        // that the full page content might bury in noise.
        foreach (var source in sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Snippet))
                extractedChunks.Add(source.Snippet);
        }

        AddAuditStep(auditSteps, "extract", $"Extracted signals from {extractedChunks.Count} text chunks across {sources.Count} sources.");
        var hours = DeepDiveHoursParser.Parse(extractedChunks);
        var extraction = DeepDiveWebExtractor.Extract(extractedChunks, sources);

        var hoursBullets = hours.Bullets.Count > 0
            ? hours.Bullets
            : ["Hours could not be found in the available web sources."];

        if (!hours.HasAnyHours)
            warnings.Add("No structured hours found. Call ahead before visiting.");
        if (hours.HasConflict)
            warnings.Add("Different sources disagree on hours - verify directly.");

        // Confidence scales with what we actually managed to extract.
        var hasUsefulData = hours.HasAnyHours ||
            !string.IsNullOrWhiteSpace(extraction.Address) ||
            !string.IsNullOrWhiteSpace(extraction.Phone) ||
            extraction.Rating.HasValue;

        if (sources.Count == 0 && !hasUsefulData && (explicitRegionTokens.Count > 0 || extractedChunks.Count == 0))
        {
            return new DeepDiveExecutionResult
            {
                Success = false,
                IsPartial = true,
                AssistantText = BuildNoGroundingResponse(cleanedQuery, effectiveLocationHint)
            };
        }

        var confidence = hasUsefulData && !hours.HasConflict
            ? DeepDiveConstants.ConfidenceMedium
            : DeepDiveConstants.ConfidenceLow;

        AddAuditStep(auditSteps, "summarize", $"Web extraction found: name={extraction.BusinessName ?? "?"}, " +
            $"addr={!string.IsNullOrWhiteSpace(extraction.Address)}, " +
            $"phone={!string.IsNullOrWhiteSpace(extraction.Phone)}, " +
            $"rating={extraction.Rating?.ToString("0.0") ?? "?"}");

        var fallbackBriefing = BuildFallbackBriefing(
            query: query,
            timezone: timezone,
            locale: locale,
            userLocationHint: effectiveLocationHint,
            now: now,
            confidence: confidence,
            hoursBullets: hoursBullets,
            extraction: extraction,
            sourceRefs: sourceRefs,
            warnings: warnings,
            auditSteps: auditSteps);

        var finalized = ValidateAndFinalize(fallbackBriefing, warnings, sourceRefs, auditSteps);
        return finalized with
        {
            AssistantText = BuildAssistantLead(finalized.Briefing!)
        };
    }

    private DeepDiveExecutionResult ValidateAndFinalize(
        DeepDiveBriefing briefing,
        List<string> warnings,
        List<SourceRef> sourceRefs,
        List<DeepDiveAuditStep> auditSteps)
    {
        var adjusted = briefing;

        if (warnings.Count > 0 && !adjusted.Cards.Any(c => c.Type.Equals("warnings", StringComparison.OrdinalIgnoreCase)))
        {
            var warningSources = sourceRefs.Count > 0
                ? sourceRefs
                : [CreateSyntheticSourceRef("system://deep-dive/warnings")];
            adjusted = adjusted with
            {
                Cards = [new DeepDiveCard
                {
                    Type = "warnings",
                    Title = "Warnings",
                    Bullets = warnings,
                    Sources = warningSources
                }, .. adjusted.Cards]
            };
        }

        if (!DeepDiveBriefingValidator.TryValidate(adjusted, out var errors))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "agent",
                Action = "DEEP_DIVE_VALIDATION_FAIL",
                Result = "error",
                Details = new Dictionary<string, object>
                {
                    ["errors"] = errors.ToArray()
                }
            });

            var fallbackSource = sourceRefs.Count > 0
                ? sourceRefs
                : [CreateSyntheticSourceRef("system://deep-dive/fallback")];

            adjusted = BuildValidationFallback(adjusted, fallbackSource, errors, auditSteps);
        }

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "DEEP_DIVE_ASSEMBLED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["confidence"] = adjusted.Hero.Confidence,
                ["card_count"] = adjusted.Cards.Count,
                ["audit_steps"] = adjusted.Audit.Count
            }
        });

        return new DeepDiveExecutionResult
        {
            Success = true,
            IsPartial = adjusted.Hero.Confidence != DeepDiveConstants.ConfidenceHigh,
            Briefing = adjusted
        };
    }

    private bool TryBuildFromPlacesPayload(
        string query,
        string timezone,
        string locale,
        string? userLocationHint,
        string? placesJson,
        List<SourceRef> sourceRefs,
        List<string> warnings,
        List<DeepDiveAuditStep> auditSteps,
        out DeepDiveBriefing briefing)
    {
        briefing = default!;
        if (string.IsNullOrWhiteSpace(placesJson))
            return false;

        // Guard: AuditedMcpToolClient returns plain "Error: ..." strings
        // when a tool is blocked (permission denied, safe mode, etc.).
        // These are not JSON and must be handled before attempting parse.
        if (placesJson.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            placesJson.StartsWith("Tool error:", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Places lookup was blocked: {placesJson}");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(placesJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errorElement.GetString()))
            {
                warnings.Add($"Places lookup failed: {errorElement.GetString()}");
                return false;
            }

            if (!root.TryGetProperty("place", out var place) || place.ValueKind != JsonValueKind.Object)
                return false;

            var now = DateTimeOffset.UtcNow;
            var title = GetString(place, "name", query);
            var address = GetString(place, "address", "");
            var phone = NormalizePhoneForDisplay(GetString(place, "phone", ""));
            var website = GetString(place, "website", "");
            var directions = GetString(place, "directionsUrl", "");
            var openNow = GetBoolean(place, "openNow");
            var weekday = GetStringArray(place, "weekdayText");
            var reviews = GetReviews(place);
            var rating = GetNullableDouble(place, "rating");
            var totalRatings = GetNullableInt(place, "userRatingsTotal");

            if (root.TryGetProperty("sources", out var sources) &&
                sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sources.EnumerateArray())
                {
                    var url = GetString(source, "url", "");
                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    sourceRefs.Add(new SourceRef
                    {
                        Name = GetString(source, "name", "Google Places"),
                        Url = url,
                        FetchedIso = GetString(source, "fetchedIso", now.ToString("O"))
                    });
                }
            }

            if (sourceRefs.Count == 0)
            {
                var mapUrl = string.IsNullOrWhiteSpace(directions)
                    ? $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(query)}"
                    : directions;
                sourceRefs.Add(new SourceRef
                {
                    Name = "Google Places",
                    Url = mapUrl,
                    FetchedIso = now.ToString("O")
                });
            }

            var todayHours = weekday.FirstOrDefault() ?? "Hours not published";
            var status = openNow.HasValue
                ? (openNow.Value ? "Open now" : "Closed now")
                : "Status unavailable";

            var hoursBullets = weekday.Count > 0
                ? weekday
                : ["No structured hours were returned by Places."];

            if (weekday.Count == 0)
                warnings.Add("Places response did not include structured weekly hours.");

            var reviewBullets = BuildReviewBullets(reviews, rating, totalRatings);
            var summaryBullets = BuildSummaryBullets(address, phone, website, rating, totalRatings);

            var linksBullets = new List<string>();
            if (!string.IsNullOrWhiteSpace(website))
                linksBullets.Add($"Website: {website}");
            if (!string.IsNullOrWhiteSpace(directions))
                linksBullets.Add($"Directions: {directions}");
            if (linksBullets.Count == 0)
                linksBullets.Add("No direct website or directions URL was returned.");

            var cards = new List<DeepDiveCard>
            {
                new()
                {
                    Type = "hours",
                    Title = "Hours",
                    Bullets = hoursBullets,
                    Sources = sourceRefs
                },
                new()
                {
                    Type = "reviews",
                    Title = "Reviews",
                    Bullets = reviewBullets,
                    Sources = sourceRefs
                },
                new()
                {
                    Type = "summary",
                    Title = "What to Expect",
                    Bullets = summaryBullets,
                    Sources = sourceRefs
                },
                new()
                {
                    Type = "links",
                    Title = "Useful Links",
                    Bullets = linksBullets,
                    Sources = sourceRefs
                }
            };

            AddAuditStep(auditSteps, "assemble", "Assembled places-first briefing cards.");

            DeepDiveMap? map = null;
            if (TryGetMap(place, out var lat, out var lng))
            {
                map = new DeepDiveMap
                {
                    Latitude = lat,
                    Longitude = lng,
                    Label = title
                };
            }

            var confidence = warnings.Count == 0
                ? DeepDiveConstants.ConfidenceHigh
                : DeepDiveConstants.ConfidenceMedium;

            briefing = _assembler.Assemble(new DeepDiveAssembleRequest
            {
                TopicKind = DeepDiveConstants.KindPlace,
                Query = query,
                Timezone = timezone,
                Locale = locale,
                UserLocationHint = userLocationHint,
                HeroTitle = title,
                Confidence = confidence,
                LastCheckedIso = now.ToString("O"),
                StatusLine = status,
                ClosesText = $"Today: {todayHours}",
                Address = address,
                Phone = phone,
                Website = website,
                DirectionsUrl = directions,
                Map = map,
                Cards = cards,
                AuditSteps = auditSteps
            });
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not parse Places payload: {ex.Message}");
            return false;
        }
    }

    private DeepDiveBriefing BuildFallbackBriefing(
        string query,
        string timezone,
        string locale,
        string? userLocationHint,
        DateTimeOffset now,
        string confidence,
        IReadOnlyList<string> hoursBullets,
        WebExtractionResult extraction,
        IReadOnlyList<SourceRef> sourceRefs,
        IReadOnlyList<string> warnings,
        IReadOnlyList<DeepDiveAuditStep> auditSteps)
    {
        var sourceList = sourceRefs.Count > 0
            ? sourceRefs
            : [CreateSyntheticSourceRef("system://deep-dive/web-fallback")];

        // Use real extracted data for cards instead of static placeholders.
        var reviewBullets  = DeepDiveWebExtractor.BuildReviewBullets(extraction);
        var summaryBullets = DeepDiveWebExtractor.BuildSummaryBullets(extraction);

        // Derive the hero title from extracted business name or fall back to query.
        // When web extraction pulled a name from an irrelevant article
        // (e.g. "Our Strike" instead of "Starbucks"), it won't share any
        // meaningful token with the cleaned query.  Fall back to the query
        // so the correct business name always appears in the response.
        var cleanedForTitle = CleanQueryForWebFallback(query);
        var heroTitle = !string.IsNullOrWhiteSpace(extraction.BusinessName)
                        && ExtractedNameMatchesQuery(extraction.BusinessName!, cleanedForTitle)
            ? extraction.BusinessName!
            : cleanedForTitle;

        // Build a meaningful status line from what we actually found.
        var closesText = hoursBullets.Count > 0 && !hoursBullets[0].Contains("could not", StringComparison.OrdinalIgnoreCase)
            ? $"Today: {hoursBullets[0]}"
            : "Hours were not found in available sources.";

        var cards = new List<DeepDiveCard>
        {
            new()
            {
                Type = "hours",
                Title = "Hours",
                Bullets = hoursBullets,
                Sources = sourceList
            },
            new()
            {
                Type = "reviews",
                Title = "Reviews",
                Bullets = reviewBullets,
                Sources = sourceList
            },
            new()
            {
                Type = "summary",
                Title = "Details",
                Bullets = summaryBullets,
                Sources = sourceList
            },
            new()
            {
                Type = "links",
                Title = "Sources",
                Bullets = sourceList.Select(s => $"{s.Name}: {s.Url}").ToList(),
                Sources = sourceList
            }
        };

        if (warnings.Count > 0)
        {
            cards.Insert(0, new DeepDiveCard
            {
                Type = "warnings",
                Title = "Warnings",
                Bullets = warnings.ToList(),
                Sources = sourceList
            });
        }

        return _assembler.Assemble(new DeepDiveAssembleRequest
        {
            TopicKind = DeepDiveConstants.KindPlace,
            Query = query,
            Timezone = timezone,
            Locale = locale,
            UserLocationHint = userLocationHint,
            HeroTitle = heroTitle,
            Confidence = confidence,
            LastCheckedIso = now.ToString("O"),
            StatusLine = confidence == DeepDiveConstants.ConfidenceLow
                ? "Verification recommended"
                : "Details from web sources",
            ClosesText = closesText,
            Address = extraction.Address ?? "",
            Phone = NormalizePhoneForDisplay(extraction.Phone),
            Website = extraction.Website ?? "",
            Cards = cards,
            AuditSteps = auditSteps
        });
    }

    private static DeepDiveBriefing BuildValidationFallback(
        DeepDiveBriefing baseBriefing,
        IReadOnlyList<SourceRef> sources,
        IReadOnlyList<string> errors,
        IReadOnlyList<DeepDiveAuditStep> auditSteps)
    {
        return new DeepDiveBriefing
        {
            Version = DeepDiveConstants.ContractVersion,
            Topic = new DeepDiveTopic
            {
                Kind = DeepDiveConstants.KindPlace,
                Query = baseBriefing.Topic.Query,
                Timezone = string.IsNullOrWhiteSpace(baseBriefing.Topic.Timezone) ? "unknown" : baseBriefing.Topic.Timezone,
                Locale = string.IsNullOrWhiteSpace(baseBriefing.Topic.Locale) ? "en-US" : baseBriefing.Topic.Locale,
                UserLocationHint = baseBriefing.Topic.UserLocationHint
            },
            Hero = new DeepDiveHero
            {
                Title = baseBriefing.Hero.Title,
                Confidence = DeepDiveConstants.ConfidenceLow,
                LastCheckedIso = DateTimeOffset.UtcNow.ToString("O"),
                StatusLine = "Validation fallback used",
                ClosesText = "Contract recovery mode.",
                Address = baseBriefing.Hero.Address,
                Phone = baseBriefing.Hero.Phone,
                Website = baseBriefing.Hero.Website,
                DirectionsUrl = baseBriefing.Hero.DirectionsUrl
            },
            Cards =
            [
                new DeepDiveCard
                {
                    Type = "warnings",
                    Title = "Warnings",
                    Bullets = errors.Select(e => $"Contract issue: {e}").ToList(),
                    Sources = sources
                },
                new DeepDiveCard
                {
                    Type = "hours",
                    Title = "Hours",
                    Bullets = ["Hours unavailable due to validation fallback."],
                    Sources = sources
                },
                new DeepDiveCard
                {
                    Type = "reviews",
                    Title = "Reviews",
                    Bullets = ["Review synthesis unavailable due to validation fallback."],
                    Sources = sources
                },
                new DeepDiveCard
                {
                    Type = "summary",
                    Title = "Summary",
                    Bullets = ["Briefing was recovered with safe defaults."],
                    Sources = sources
                }
            ],
            Audit = auditSteps
        };
    }

    private static string BuildAssistantLead(DeepDiveBriefing briefing)
    {
        var parts = new List<string>();

        // Title + status
        var leadTitle = NormalizeHeroTitleForLead(briefing.Hero.Title);
        parts.Add($"**{leadTitle}**");

        if (!string.IsNullOrWhiteSpace(briefing.Hero.StatusLine))
            parts.Add(briefing.Hero.StatusLine);

        // Today's hours (the most common reason someone asks)
        if (!string.IsNullOrWhiteSpace(briefing.Hero.ClosesText))
            parts.Add(briefing.Hero.ClosesText);

        // Address for context
        if (!string.IsNullOrWhiteSpace(briefing.Hero.Address))
            parts.Add($"Address: {briefing.Hero.Address}");

        // Phone
        var normalizedPhone = NormalizePhoneForDisplay(briefing.Hero.Phone);
        if (!string.IsNullOrWhiteSpace(normalizedPhone))
            parts.Add($"Phone: {normalizedPhone}");

        // Pull a true rating line from the reviews card when available.
        // Avoid matching arbitrary snippets that happen to include '/5'.
        var reviewCard = briefing.Cards.FirstOrDefault(c =>
            c.Type.Equals("reviews", StringComparison.OrdinalIgnoreCase));
        if (reviewCard is not null)
        {
            var ratingBullet = reviewCard.Bullets.FirstOrDefault(b =>
                b.StartsWith("Rating:", StringComparison.OrdinalIgnoreCase) ||
                b.StartsWith("Average rating:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(ratingBullet))
                parts.Add(ratingBullet);
        }

        var warningsCard = briefing.Cards.FirstOrDefault(c =>
            c.Type.Equals("warnings", StringComparison.OrdinalIgnoreCase));
        if (warningsCard is not null)
        {
            foreach (var providerLine in BuildProviderFailureLeadLines(warningsCard.Bullets))
                parts.Add(providerLine);
        }

        if (briefing.Hero.Confidence.Equals(DeepDiveConstants.ConfidenceLow, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(!string.IsNullOrWhiteSpace(normalizedPhone)
                ? "Current open status is unknown from the available sources. Call the store to confirm current hours."
                : "Current open status is unknown from the available sources. Check the listed source before visiting.");
        }

        if (!string.IsNullOrWhiteSpace(briefing.Hero.Website))
            parts.Add("Use the listed website to confirm current hours before visiting.");

        var sourceDomainsLine = BuildSourceDomainsLeadLine(briefing);
        if (!string.IsNullOrWhiteSpace(sourceDomainsLine))
            parts.Add(sourceDomainsLine);

        parts.Add("Briefing summary: hours and review details are based on currently available web sources.");

        return string.Join("\n", parts);
    }

    private static string BuildSourceDomainsLeadLine(DeepDiveBriefing briefing)
    {
        var domains = briefing.Cards
            .SelectMany(card => card.Sources)
            .Select(source => source.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return "";

                var host = uri.Host.Trim().ToLowerInvariant();
                return host.StartsWith("www.", StringComparison.Ordinal)
                    ? host[4..]
                    : host;
            })
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (domains.Count == 0)
            return string.Empty;

        return $"Sources checked: {string.Join(", ", domains)}.";
    }

    private static IReadOnlyList<string> BuildProviderFailureLeadLines(IReadOnlyList<string> warnings)
    {
        var lines = new List<string>();

        if (warnings.Any(w => w.Contains("0 results", StringComparison.OrdinalIgnoreCase)))
            lines.Add("The fallback search came back with 0 results for this query.");

        return lines;
    }

    private static string NormalizeHeroTitleForLead(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title;

        var normalized = title.Trim()
            .Replace("\u2019", "'", StringComparison.Ordinal)
            .Replace("`", "'", StringComparison.Ordinal);

        // Normalize possessive/apostrophe variants so token matching remains stable.
        normalized = Regex.Replace(normalized, @"(?<=\p{L})'(?=\p{L})", "");
        return normalized;
    }

    private static string NormalizePhoneForDisplay(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return "";

        return Regex.Replace(phone, @"\s+", " ").Trim();
    }

    private static List<string> BuildReviewBullets(
        IReadOnlyList<string> reviews,
        double? rating,
        int? userRatingsTotal)
    {
        var bullets = new List<string>();
        if (rating.HasValue)
        {
            var total = userRatingsTotal.HasValue ? $" across {userRatingsTotal.Value} ratings" : "";
            bullets.Add($"Average rating: {rating.Value:0.0}{total}.");
        }
        else
        {
            bullets.Add("Average rating was not published by the provider.");
        }

        if (reviews.Count == 0)
        {
            bullets.Add("No review snippets were returned.");
            bullets.Add("Check the source links for the latest customer comments.");
            return bullets;
        }

        var positiveSignals = reviews.Count(r => ContainsAny(r, "great", "friendly", "fast", "clean", "excellent", "amazing"));
        var negativeSignals = reviews.Count(r => ContainsAny(r, "slow", "rude", "bad", "expensive", "crowded", "dirty"));

        bullets.Add(positiveSignals >= negativeSignals
            ? "People often praise service quality and staff friendliness."
            : "Feedback is mixed; service consistency is a recurring concern.");

        bullets.Add(negativeSignals > 0
            ? "Common complaints mention wait times or inconsistent experiences."
            : "No dominant complaint theme surfaced in sampled snippets.");

        return bullets;
    }

    private static IReadOnlyList<string> BuildSummaryBullets(
        string address,
        string phone,
        string website,
        double? rating,
        int? userRatingsTotal)
    {
        var bullets = new List<string>();
        if (!string.IsNullOrWhiteSpace(address))
            bullets.Add($"Address: {address}");
        var normalizedPhone = NormalizePhoneForDisplay(phone);
        if (!string.IsNullOrWhiteSpace(normalizedPhone))
            bullets.Add($"Phone: {normalizedPhone}");
        if (!string.IsNullOrWhiteSpace(website))
            bullets.Add($"Website listed for quick verification: {website}");

        if (rating.HasValue)
        {
            var ratingsDetail = userRatingsTotal.HasValue ? $" from {userRatingsTotal.Value} ratings" : "";
            bullets.Add($"Reputation signal: {rating.Value:0.0}/5{ratingsDetail}.");
        }

        if (bullets.Count == 0)
            bullets.Add("Only limited business profile details were returned.");

        return bullets;
    }

    // JSON helpers delegated to DeepDiveJsonHelpers to keep this file focused on orchestration.
    private static string GetString(JsonElement el, string prop, string fb) => DeepDiveJsonHelpers.GetString(el, prop, fb);
    private static bool? GetBoolean(JsonElement el, string prop) => DeepDiveJsonHelpers.GetBoolean(el, prop);
    private static double? GetNullableDouble(JsonElement el, string prop) => DeepDiveJsonHelpers.GetNullableDouble(el, prop);
    private static int? GetNullableInt(JsonElement el, string prop) => DeepDiveJsonHelpers.GetNullableInt(el, prop);
    private static IReadOnlyList<string> GetStringArray(JsonElement el, string prop) => DeepDiveJsonHelpers.GetStringArray(el, prop);
    private static List<string> GetReviews(JsonElement place) => DeepDiveJsonHelpers.GetReviews(place);
    private static bool TryGetMap(JsonElement place, out double lat, out double lng) => DeepDiveJsonHelpers.TryGetMap(place, out lat, out lng);
    private static bool ContainsAny(string text, params string[] signals) => DeepDiveJsonHelpers.ContainsAny(text, signals);

    private static void AddAuditStep(
        ICollection<DeepDiveAuditStep> auditSteps,
        string step,
        string detail)
    {
        auditSteps.Add(new DeepDiveAuditStep
        {
            Step = step,
            Detail = detail,
            TimestampIso = DateTimeOffset.UtcNow.ToString("O"),
            Sources = []
        });
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;
        return value[..maxChars] + "...";
    }

    private static SourceRef CreateSyntheticSourceRef(string url)
    {
        return new SourceRef
        {
            Name = "Runtime",
            Url = url,
            FetchedIso = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    /// <summary>
    /// Returns true when the extracted business name shares at least one
    /// significant word (3+ chars) with the cleaned query — indicating
    /// the extraction came from a relevant source rather than an unrelated
    /// article like "Our Strike" for a "Starbucks" query.
    /// Uses whole-word matching to avoid false positives like "and" in "portland".
    /// </summary>
    internal static bool ExtractedNameMatchesQuery(string extractedName, string cleanedQuery)
    {
        if (string.IsNullOrWhiteSpace(extractedName) || string.IsNullOrWhiteSpace(cleanedQuery))
            return false;

        var nameTokens = extractedName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(',', '.', '?', '!', ':', ';', '"', '\'').ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var queryTokens = cleanedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(',', '.', '?', '!', ':', ';', '"', '\'').ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Exclude stop words that appear everywhere and cause false positives
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "are", "but", "not", "you", "all",
            "can", "had", "her", "his", "was", "one", "our", "out",
            "has", "its", "how", "did", "get", "let", "say", "she",
            "too", "use", "way", "who", "new", "now", "old", "see"
        };

        return nameTokens.Any(t => !stopWords.Contains(t) && queryTokens.Contains(t));
    }

    /// <summary>
    /// Strips conversational wrappers and redundant keywords from a
    /// user message to produce a search-engine-friendly query.
    ///
    /// "Can you tell me what the operating hours of Trader Joe's in Portland is?"
    ///  -> "Trader Joe's Portland"
    ///
    /// "When does Trader Joe's in Portland OR open?"
    ///  -> "Trader Joe's Portland OR"
    /// </summary>
    internal static string CleanQueryForWebFallback(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var cleaned = StripEmbeddedInstructionScaffold(raw).Trim();

        cleaned = Regex.Replace(cleaned, @"^deep\s+dive\s+", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\bwhat to expect\b", string.Empty, RegexOptions.IgnoreCase);
        cleaned = cleaned.Replace("+", " ", StringComparison.Ordinal);

        // Strip common leading question scaffolding.
        cleaned = CleanLeadPhraseRegex().Replace(cleaned, "").Trim();

        // Strip trailing question marks and filler.
        cleaned = cleaned.TrimEnd('?', '.', '!', ',');
        cleaned = CleanTrailRegex().Replace(cleaned, "").Trim();
        cleaned = CleanDanglingCopulaRegex().Replace(cleaned, "").Trim();

        // Remove redundant words that we're going to append anyway.
        cleaned = CleanRedundantKeywordsRegex().Replace(cleaned, " ").Trim();
        cleaned = CleanDanglingConnectorRegex().Replace(cleaned, " ").Trim();

        // Collapse whitespace.
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // If we stripped too aggressively, fall back to the original.
        return cleaned.Length >= 4 ? cleaned : raw.Trim().TrimEnd('?', '.', '!');
    }

    private static string StripEmbeddedInstructionScaffold(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var markers = new[]
        {
            "Verification requirement:",
            "Do not answer from memory alone",
            "If snippets are insufficient"
        };

        foreach (var marker in markers)
        {
            var idx = cleaned.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                cleaned = cleaned[..idx].TrimEnd();
        }

        return cleaned.Trim();
    }

    private static string BuildNoGroundingResponse(string cleanedQuery, string? locationHint)
    {
        var subject = string.IsNullOrWhiteSpace(cleanedQuery) ? "that place" : cleanedQuery;
        var locationClause = string.IsNullOrWhiteSpace(locationHint)
            ? string.Empty
            : $" near {locationHint}";

        return $"I couldn't verify live hours, reviews, or contact details for {subject}{locationClause} from the available sources right now. Try a more specific business name, or try again later when search providers are responding.";
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?:can you (?:tell me|find(?: me)?|check|look up) )?(?:(?:is|are)\s+)?(?:(?:what (?:is|are)|what time(?:\s+(?:does|do|is|are))?)\s+)?(?:the )?(?:operating )?(?:hours (?:of|for) )?(?:when (?:does|do|is|will) )?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex CleanLeadPhraseRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?:\s+(?:open|close|right now|today|tonight|tomorrow|this week|currently))+\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex CleanTrailRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:operating|hours|reviews|address|phone|website|number|directions|open|closed)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex CleanRedundantKeywordsRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:is|are|does|do)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex CleanDanglingCopulaRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?:\b(?:with|and|plus)\b\s*)+$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex CleanDanglingConnectorRegex();

    private static bool IsNavigablePlaceFallbackSource(SourceItem source)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            return false;

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.Trim().ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        if (host.Contains("news.google.com", StringComparison.Ordinal) ||
            host.Equals("news.google.com", StringComparison.Ordinal) ||
            host.Contains("google.com", StringComparison.Ordinal) && uri.AbsolutePath.Contains("/rss/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsUsefulPlaceFallbackSource(
        SourceItem source,
        string cleanedQuery,
        IReadOnlyList<string>? explicitRegionTokens = null)
    {
        if (!IsNavigablePlaceFallbackSource(source))
            return false;

        var combined = $"{source.Title} {source.Snippet} {source.Url}".Trim();
        if (string.IsNullOrWhiteSpace(combined))
            return false;

        explicitRegionTokens ??= ExtractExplicitRegionTokens(cleanedQuery);
        if (explicitRegionTokens.Count > 0)
        {
            var lowerCombined = combined.ToLowerInvariant();
            if (!explicitRegionTokens.Any(token => lowerCombined.Contains(token, StringComparison.Ordinal)))
                return false;
        }

        return ExtractedNameMatchesQuery(combined, cleanedQuery) ||
               combined.Contains("hour", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("address", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("phone", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExtractExplicitRegionTokens(string cleanedQuery)
    {
        if (string.IsNullOrWhiteSpace(cleanedQuery))
            return [];

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(cleanedQuery, @"\b[A-Z]{2}\b"))
        {
            var value = match.Value.Trim();
            if (value.Length == 2)
                tokens.Add(value.ToLowerInvariant());
        }

        var lowered = cleanedQuery.ToLowerInvariant();
        foreach (var stateName in new[]
                 {
                     "oregon", "washington", "california", "idaho", "nevada", "utah",
                     "arizona", "montana", "wyoming", "colorado", "texas", "florida",
                     "new york", "illinois", "indiana", "ohio"
                 })
        {
            if (lowered.Contains(stateName, StringComparison.Ordinal))
                tokens.Add(stateName);
        }

        return tokens.ToList();
    }

    /// <summary>
    /// Uses configured user location for place queries unless the query
    /// already contains a city/state reference from the location hint.
    /// "William's Flowers" -> appends "Olympia, WA"
    /// "Trader Joe's Portland OR" -> already contains a city, skips hint
    /// </summary>
    private static string? ResolveLocationHintForQuery(
        string rawQuery,
        string cleanedQuery,
        string? userLocationHint)
    {
        if (string.IsNullOrWhiteSpace(userLocationHint))
            return null;

        // Explicit proximity cues always use the hint.
        var lower = rawQuery.ToLowerInvariant();
        if (lower.Contains("near me", StringComparison.Ordinal) ||
            lower.Contains("nearby", StringComparison.Ordinal) ||
            lower.Contains("around me", StringComparison.Ordinal))
        {
            return userLocationHint;
        }

        if (LooksLikeSelfContainedPlaceQuery(cleanedQuery))
            return null;

        // Check if the query already mentions the configured location
        // (city or state). Split the hint into tokens and check for any
        // significant word (skip short tokens like "WA" -> 2 chars is OK).
        // Examples:
        //   hint="Olympia, WA"  -> ["olympia", "wa"]
        //   query="Trader Joe's Olympia" -> contains "olympia" -> skip
        //   query="William's Flowers"    -> no match -> include hint
        var hintTokens = userLocationHint
            .ToLowerInvariant()
            .Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries);

        var queryLower = cleanedQuery.ToLowerInvariant();
        var queryAlreadyHasLocation = hintTokens.Any(token =>
            token.Length >= 2 && queryLower.Contains(token, StringComparison.Ordinal));

        return queryAlreadyHasLocation ? null : userLocationHint;
    }

    private static bool LooksLikeSelfContainedPlaceQuery(string cleanedQuery)
    {
        if (string.IsNullOrWhiteSpace(cleanedQuery))
            return false;

        var tokens = cleanedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim(',', '.', '?', '!', ':', ';', '"', '\''))
            .Where(token => token.Length > 1)
            .ToList();

        if (tokens.Count < 2)
            return false;

        var genericTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hours", "reviews", "address", "phone", "website", "open", "closed",
            "close", "today", "tonight", "tomorrow", "details"
        };

        var signalTokens = tokens.Count(token => !genericTokens.Contains(token));
        return signalTokens >= 2;
    }

    private static int ParseIntEnv(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(raw, out var parsed))
            return fallback;
        return Math.Clamp(parsed, min, max);
    }
}

public sealed record DeepDiveExecutionResult
{
    public bool Success { get; init; }
    public bool IsPartial { get; init; }
    public string AssistantText { get; init; } = "";
    public DeepDiveBriefing? Briefing { get; init; }
}

