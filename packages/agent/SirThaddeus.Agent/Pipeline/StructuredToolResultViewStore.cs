using System.Globalization;
using System.Text;
using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Turn-local, scorer-blind storage for large structured read results. Raw
/// values remain in the ordinary tool-call record; this store only controls
/// how much of that already-authorized value is placed back into model history.
/// </summary>
internal sealed class StructuredToolResultViewStore
{
    internal const string ToolName = "tool_result_view";
    internal const int MinimumResultBytes = 8 * 1024;
    internal const int MaximumViewBytes = 8 * 1024;
    private const int MaximumRows = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, StoredResult> _results = new(StringComparer.Ordinal);
    private int _nextHandle = 1;

    internal bool HasResults => _results.Count > 0;

    internal static ToolDefinition Definition { get; } = new()
    {
        Function = new FunctionDefinition
        {
            Name = ToolName,
            Description =
                "Inspect a large JSON result already returned this turn. Use first_last for endpoints, " +
                "slice/sample for rows, filter for matching rows, aggregate for numeric count/min/max/sum/average, " +
                "or schema for fields. Results are bounded; the original remains unchanged in audit evidence.",
            Parameters = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["handle"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "Opaque handle from a structured tool-result preview.",
                    },
                    ["operation"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "schema", "sample", "first_last", "slice", "filter", "aggregate" },
                    },
                    ["offset"] = new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = 0 },
                    ["limit"] = new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaximumRows },
                    ["field"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["operator"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "eq", "ne", "lt", "lte", "gt", "gte", "contains" },
                    },
                    ["value"] = new Dictionary<string, object>
                    {
                        ["description"] = "Filter comparison value; strings, numbers, and booleans are accepted.",
                    },
                    ["statistic"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "count", "min", "max", "sum", "average" },
                    },
                },
                ["required"] = new[] { "handle", "operation" },
                ["additionalProperties"] = false,
            },
        },
    };

    internal bool TryProject(
        string toolName,
        string arguments,
        string rawResult,
        out StructuredToolResultProjection projection,
        out string reason)
    {
        projection = default!;
        var safeRawResult = rawResult ?? string.Empty;
        var originalBytes = Encoding.UTF8.GetByteCount(safeRawResult);
        if (originalBytes < MinimumResultBytes)
        {
            reason = "below-byte-threshold";
            return false;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(safeRawResult);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                reason = "root-not-array";
                return false;
            }

            if (document.RootElement.GetArrayLength() < 2)
            {
                reason = "insufficient-items";
                return false;
            }

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            reason = "invalid-json";
            return false;
        }

        var handle = $"result_{_nextHandle++}";
        var stored = new StoredResult(
            handle,
            toolName,
            ParseArguments(arguments),
            root,
            BuildSchema(root));
        _results.Add(handle, stored);

        var preview = BuildPreview(stored);

        projection = new StructuredToolResultProjection(
            handle,
            preview,
            originalBytes,
            Encoding.UTF8.GetByteCount(preview),
            root.GetArrayLength());
        reason = "eligible-large-json-array";
        return true;
    }

    private static string BuildPreview(StoredResult stored)
    {
        var count = stored.Root.GetArrayLength();
        var full = new Dictionary<string, object?>
        {
            ["kind"] = "structured_tool_result",
            ["handle"] = stored.Handle,
            ["source"] = new Dictionary<string, object?>
            {
                ["tool"] = stored.SourceTool,
                ["arguments"] = stored.SourceArguments,
            },
            ["root_type"] = "array",
            ["item_count"] = count,
            ["schema"] = stored.Schema,
            ["first"] = stored.Root[0],
            ["last"] = stored.Root[count - 1],
            ["available_views"] = new[] { "schema", "sample", "first_last", "slice", "filter", "aggregate" },
            ["instruction"] = "Use tool_result_view with this handle only if the preview is insufficient.",
        };
        var json = JsonSerializer.Serialize(full, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) <= MaximumViewBytes)
            return json;

        var reduced = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = "structured_tool_result",
            ["handle"] = stored.Handle,
            ["source_tool"] = stored.SourceTool,
            ["root_type"] = "array",
            ["item_count"] = count,
            ["schema"] = stored.Schema,
            ["sample_omitted"] = "Sample rows exceeded the bounded preview. Use a narrow view.",
            ["available_views"] = new[] { "schema", "sample", "first_last", "slice", "filter", "aggregate" },
        }, JsonOptions);
        if (Encoding.UTF8.GetByteCount(reduced) <= MaximumViewBytes)
            return reduced;

        return JsonSerializer.Serialize(new
        {
            kind = "structured_tool_result",
            handle = stored.Handle,
            root_type = "array",
            item_count = count,
            preview_omitted = "Metadata exceeded the bounded preview. Use schema or a narrow view.",
        }, JsonOptions);
    }

    internal bool TryExecute(
        string toolName,
        string arguments,
        out ToolCallOutcome outcome,
        out string operation,
        out int resultBytes)
    {
        outcome = default!;
        operation = "unknown";
        resultBytes = 0;
        if (!string.Equals(toolName, ToolName, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object ||
                !TryGetString(args, "handle", out var handle) ||
                !TryGetString(args, "operation", out operation))
            {
                outcome = Error("invalid_arguments", "handle and operation are required");
            }
            else if (!_results.TryGetValue(handle, out var stored))
            {
                outcome = Error("unknown_handle", "the handle is not available in this turn");
            }
            else
            {
                outcome = Execute(stored, args, operation);
            }
        }
        catch (JsonException)
        {
            outcome = Error("invalid_arguments", "arguments must be one JSON object");
        }

        resultBytes = Encoding.UTF8.GetByteCount(outcome.ResultText);
        return true;
    }

    private static ToolCallOutcome Execute(StoredResult stored, JsonElement args, string operation)
    {
        var normalized = operation.Trim().ToLowerInvariant();
        return normalized switch
        {
            "schema" => Ok(stored, normalized, new Dictionary<string, object?>
            {
                ["item_count"] = stored.Root.GetArrayLength(),
                ["schema"] = stored.Schema,
            }),
            "sample" => Rows(stored, normalized, 0, ReadLimit(args, 3)),
            "first_last" => FirstLast(stored),
            "slice" => Rows(stored, normalized, ReadOffset(args), ReadLimit(args, 10)),
            "filter" => Filter(stored, args),
            "aggregate" => Aggregate(stored, args),
            _ => Error("unsupported_operation", "operation must be schema, sample, first_last, slice, filter, or aggregate"),
        };
    }

    private static ToolCallOutcome FirstLast(StoredResult stored)
    {
        var count = stored.Root.GetArrayLength();
        return Ok(stored, "first_last", new Dictionary<string, object?>
        {
            ["item_count"] = count,
            ["first"] = stored.Root[0],
            ["last"] = stored.Root[count - 1],
        });
    }

    private static ToolCallOutcome Rows(StoredResult stored, string operation, int offset, int limit)
    {
        var count = stored.Root.GetArrayLength();
        var safeOffset = Math.Clamp(offset, 0, count);
        var rows = stored.Root.EnumerateArray().Skip(safeOffset).Take(limit).Select(item => item.Clone()).ToList();
        return Ok(stored, operation, new Dictionary<string, object?>
        {
            ["item_count"] = count,
            ["offset"] = safeOffset,
            ["returned_count"] = rows.Count,
            ["rows"] = rows,
        });
    }

    private static ToolCallOutcome Filter(StoredResult stored, JsonElement args)
    {
        if (!TryGetString(args, "field", out var field) ||
            !TryGetString(args, "operator", out var comparison) ||
            !args.TryGetProperty("value", out var expected))
        {
            return Error("invalid_arguments", "filter requires field, operator, and value");
        }

        var limit = ReadLimit(args, 10);
        var matches = new List<JsonElement>();
        var matchCount = 0;
        foreach (var item in stored.Root.EnumerateArray())
        {
            if (!TryGetProperty(item, field, out var actual) || !Matches(actual, comparison, expected))
                continue;
            matchCount++;
            if (matches.Count < limit)
                matches.Add(item.Clone());
        }

        return Ok(stored, "filter", new Dictionary<string, object?>
        {
            ["field"] = field,
            ["operator"] = comparison,
            ["matched_count"] = matchCount,
            ["returned_count"] = matches.Count,
            ["rows"] = matches,
        });
    }

    private static ToolCallOutcome Aggregate(StoredResult stored, JsonElement args)
    {
        if (!TryGetString(args, "statistic", out var statistic))
            return Error("invalid_arguments", "aggregate requires statistic");

        var normalized = statistic.Trim().ToLowerInvariant();
        if (normalized == "count")
        {
            return Ok(stored, "aggregate", new Dictionary<string, object?>
            {
                ["statistic"] = "count",
                ["value"] = stored.Root.GetArrayLength(),
            });
        }

        if (!TryGetString(args, "field", out var field))
            return Error("invalid_arguments", "numeric aggregate requires field");

        var values = new List<double>();
        foreach (var item in stored.Root.EnumerateArray())
        {
            if (TryGetProperty(item, field, out var value) && TryReadNumber(value, out var number))
                values.Add(number);
        }

        if (values.Count == 0)
            return Error("no_numeric_values", $"field '{field}' has no numeric values");

        double aggregate;
        switch (normalized)
        {
            case "min": aggregate = values.Min(); break;
            case "max": aggregate = values.Max(); break;
            case "sum": aggregate = values.Sum(); break;
            case "average": aggregate = values.Average(); break;
            default: return Error("invalid_arguments", "statistic must be count, min, max, sum, or average");
        }

        return Ok(stored, "aggregate", new Dictionary<string, object?>
        {
            ["field"] = field,
            ["statistic"] = normalized,
            ["numeric_count"] = values.Count,
            ["value"] = aggregate,
        });
    }

    private static ToolCallOutcome Ok(StoredResult stored, string operation, Dictionary<string, object?> data)
    {
        data["handle"] = stored.Handle;
        data["operation"] = operation;
        return new ToolCallOutcome(SerializeBounded(data), Ok: true, Error: null);
    }

    private static ToolCallOutcome Error(string code, string message) =>
        new(
            JsonSerializer.Serialize(new { error = new { code, message } }, JsonOptions),
            Ok: false,
            Error: code);

    private static string SerializeBounded(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) <= MaximumViewBytes)
            return json;

        return JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "view_too_large",
                message = "Use a smaller limit, a narrower filter, or aggregate instead.",
            },
        }, JsonOptions);
    }

    private static IReadOnlyDictionary<string, string> BuildSchema(JsonElement root)
    {
        var kinds = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in root.EnumerateArray().Take(32))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                AddKind(kinds, "$item", Kind(item));
                continue;
            }

            foreach (var property in item.EnumerateObject())
            {
                if (!kinds.ContainsKey(property.Name) && kinds.Count >= 64)
                    continue;
                AddKind(kinds, property.Name, Kind(property.Value));
            }
        }

        return kinds.ToDictionary(
            pair => pair.Key,
            pair => string.Join('|', pair.Value.OrderBy(value => value, StringComparer.Ordinal)),
            StringComparer.Ordinal);
    }

    private static void AddKind(IDictionary<string, HashSet<string>> kinds, string name, string kind)
    {
        if (!kinds.TryGetValue(name, out var values))
        {
            values = new HashSet<string>(StringComparer.Ordinal);
            kinds[name] = values;
        }
        values.Add(kind);
    }

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };

    private static object? ParseArguments(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    private static int ReadOffset(JsonElement args) =>
        args.TryGetProperty("offset", out var value) && value.TryGetInt32(out var offset)
            ? Math.Max(0, offset)
            : 0;

    private static int ReadLimit(JsonElement args, int fallback) =>
        args.TryGetProperty("limit", out var value) && value.TryGetInt32(out var limit)
            ? Math.Clamp(limit, 1, MaximumRows)
            : fallback;

    private static bool TryGetString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        result = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result);
    }

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in value.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            result = property.Value;
            return true;
        }
        return false;
    }

    private static bool Matches(JsonElement actual, string comparison, JsonElement expected)
    {
        var normalized = comparison.Trim().ToLowerInvariant();
        if (TryReadNumber(actual, out var actualNumber) && TryReadNumber(expected, out var expectedNumber))
        {
            return normalized switch
            {
                "eq" => actualNumber.Equals(expectedNumber),
                "ne" => !actualNumber.Equals(expectedNumber),
                "lt" => actualNumber < expectedNumber,
                "lte" => actualNumber <= expectedNumber,
                "gt" => actualNumber > expectedNumber,
                "gte" => actualNumber >= expectedNumber,
                _ => false,
            };
        }

        var actualText = ScalarText(actual);
        var expectedText = ScalarText(expected);
        return normalized switch
        {
            "eq" => string.Equals(actualText, expectedText, StringComparison.OrdinalIgnoreCase),
            "ne" => !string.Equals(actualText, expectedText, StringComparison.OrdinalIgnoreCase),
            "contains" => actualText.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool TryReadNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
            return true;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return true;
        number = 0;
        return false;
    }

    private static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Null => "null",
        _ => value.GetRawText(),
    };

    private sealed record StoredResult(
        string Handle,
        string SourceTool,
        object? SourceArguments,
        JsonElement Root,
        IReadOnlyDictionary<string, string> Schema);
}

internal sealed record StructuredToolResultProjection(
    string Handle,
    string Preview,
    int OriginalBytes,
    int PreviewBytes,
    int ItemCount);
