using SirThaddeus.ObservationSpec;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for the ObservationSpec package.
/// </summary>
public class ObservationSpecTests
{
    // ─────────────────────────────────────────────────────────────────
    // Serialization Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateTemplate_ProducesValidJson()
    {
        var template = SpecSerializer.CreateTemplateJson();
        
        Assert.Contains("\"version\"", template);
        Assert.Contains("\"target\"", template);
        Assert.Contains("\"check\"", template);
        Assert.Contains("\"schedule\"", template);
        Assert.Contains("\"notify\"", template);
        Assert.Contains("\"limits\"", template);
    }

    [Fact]
    public void Serialize_Deserialize_Roundtrip()
    {
        var original = ObservationSpecDocument.CreateTemplate();
        var json = SpecSerializer.Serialize(original);
        var deserialized = SpecSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Target.Url, deserialized.Target.Url);
        Assert.Equal(original.Check.Value, deserialized.Check.Value);
    }

    [Fact]
    public void TryDeserialize_InvalidJson_ReturnsFalse()
    {
        var result = SpecSerializer.TryDeserialize("{ invalid json }", out var spec, out var error);

        Assert.False(result);
        Assert.Null(spec);
        Assert.NotNull(error);
        Assert.Contains("JSON", error);
    }

    // ─────────────────────────────────────────────────────────────────
    // Validator Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidSpec_ReturnsValid()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec();

        var result = validator.Validate(spec);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_UnsupportedVersion_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with { Version = "99.0" };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("version"));
    }

    [Fact]
    public void Validate_EmptyUrl_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Target = CreateValidSpec().Target with { Url = "" }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("URL"));
    }

    [Fact]
    public void Validate_InvalidUrl_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Target = CreateValidSpec().Target with { Url = "not-a-url" }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("URL"));
    }

    [Fact]
    public void Validate_UnsafeMethod_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Target = CreateValidSpec().Target with { Method = "POST" }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("method"));
    }

    [Fact]
    public void Validate_IntervalTooShort_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Schedule = new ObservationSchedule { Interval = "1m" }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Interval"));
    }

    [Fact]
    public void Validate_InvalidRegex_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Check = new ObservationCheck
            {
                Type = CheckType.RegexMatch,
                Value = "[invalid(regex"
            }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("regex"));
    }

    [Fact]
    public void Validate_JsonPathWithoutPath_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Check = new ObservationCheck
            {
                Type = CheckType.JsonPathEquals,
                Value = "expected-value"
                // Missing Path property
            }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("path"));
    }

    [Fact]
    public void Validate_NoNotifyChannels_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Notify = new ObservationNotify { OnMatch = [] }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("channel"));
    }

    [Fact]
    public void Validate_MaxChecksZero_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Limits = new ObservationLimits { MaxChecks = 0 }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("max_checks"));
    }

    [Fact]
    public void Validate_MaxChecksTooHigh_ReturnsError()
    {
        var validator = new ObservationSpecValidator();
        var spec = CreateValidSpec() with
        {
            Limits = new ObservationLimits { MaxChecks = 999999 }
        };

        var result = validator.Validate(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("10000"));
    }

    // ─────────────────────────────────────────────────────────────────
    // Explainer Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Explain_ContainsKeyFacts()
    {
        var explainer = new ObservationSpecExplainer();
        var spec = CreateValidSpec();

        var explanation = explainer.Explain(spec);

        // Contains target URL
        Assert.Contains("https://example.com/stock", explanation);

        // Contains check value
        Assert.Contains("In Stock", explanation);

        // Contains interval
        Assert.Contains("30 minutes", explanation);

        // Contains notification type
        Assert.Contains("Desktop notification", explanation);

        // Contains max checks
        Assert.Contains("500", explanation);
    }

    [Fact]
    public void Explain_GeneratesPlainEnglishSummary()
    {
        var explainer = new ObservationSpecExplainer();
        var spec = CreateValidSpec();

        var explanation = explainer.Explain(spec);

        Assert.Contains("IN PLAIN ENGLISH", explanation);
        Assert.Contains("Check", explanation);
        Assert.Contains("every", explanation);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static ObservationSpecDocument CreateValidSpec() => new()
    {
        Version = "1.0",
        Target = new ObservationTarget
        {
            Type = TargetType.WebPage,
            Url = "https://example.com/stock",
            Method = "GET"
        },
        Check = new ObservationCheck
        {
            Type = CheckType.TextContains,
            Value = "In Stock",
            Scope = "visible_text"
        },
        Schedule = new ObservationSchedule
        {
            Interval = "30m",
            Jitter = "±5m"
        },
        Notify = new ObservationNotify
        {
            OnMatch = ["local_notification"],
            Once = true
        },
        Limits = new ObservationLimits
        {
            MaxChecks = 500,
            ExpiresAt = DateTimeOffset.UtcNow.AddMonths(1)
        }
    };
}
