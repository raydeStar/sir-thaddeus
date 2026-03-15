using System.Text.Json;
using System.Text.Json.Serialization;
using SirThaddeus.Config;
using SirThaddeus.Memory;
using SirThaddeus.PersonalityEngine.Profiles;

internal static partial class RuntimeApiServer
{
    private static string BuildProfileDocumentJson(ProfileCard profile)
    {
        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(profile.ProfileJson) ? "{}" : profile.ProfileJson, EditableDocumentReadOptions);
            data = doc.RootElement.Clone();
        }
        catch
        {
            data = ParseJsonElement("{}");
        }

        var document = new EditableProfileCardDocument
        {
            ProfileId = profile.ProfileId,
            Kind = string.IsNullOrWhiteSpace(profile.Kind) ? "user" : profile.Kind,
            DisplayName = profile.DisplayName,
            Relationship = profile.Relationship,
            Aliases = SplitAliases(profile.Aliases).ToArray(),
            Data = data
        };

        return JsonSerializer.Serialize(document, EditableDocumentJsonOptions);
    }

    private static string CreateProfileTemplateJson(string profileId)
    {
        var document = new EditableProfileCardDocument
        {
            ProfileId = profileId,
            Kind = "user",
            DisplayName = "New Profile",
            Relationship = "",
            Aliases = [profileId],
            Data = ParseJsonElement("""
                {
                  "preferred_name": "Preferred name",
                  "pronouns": "they/them",
                  "timezone": "America/Denver",
                  "location": "Denver, CO",
                  "style": "Direct, practical, friendly",
                  "about_me": "Short summary for this profile",
                  "highlight": "One useful detail to remember",
                  "notes": "Anything else worth keeping handy",
                  "never_mention": []
                }
                """)
        };

        return JsonSerializer.Serialize(document, EditableDocumentJsonOptions);
    }

    private static bool TryParseProfileDocument(string? documentJson, out ProfileCard profile, out string error)
    {
        profile = new ProfileCard
        {
            ProfileId = "",
            DisplayName = ""
        };
        error = "";

        if (string.IsNullOrWhiteSpace(documentJson))
        {
            error = "DocumentJson is required.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(documentJson, EditableDocumentReadOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Profile document must be a JSON object.";
                return false;
            }

            var profileId = ReadRequiredString(root, "profile_id", out error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            var displayName = ReadRequiredString(root, "display_name", out error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            var kind = ReadOptionalString(root, "kind");
            kind = string.IsNullOrWhiteSpace(kind) ? "user" : kind.Trim().ToLowerInvariant();
            if (!string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kind, "person", StringComparison.OrdinalIgnoreCase))
            {
                error = "Profile kind must be 'user' or 'person'.";
                return false;
            }

            var relationship = ReadOptionalString(root, "relationship");
            var aliases = ReadAliases(root);
            var data = root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind != JsonValueKind.Null
                ? dataNode
                : ParseJsonElement("{}");
            if (data.ValueKind != JsonValueKind.Object)
            {
                error = "Profile document field 'data' must be a JSON object.";
                return false;
            }

            var profileJson = JsonSerializer.Serialize(data, EditableDocumentJsonOptions);
            profile = new ProfileCard
            {
                ProfileId = profileId,
                Kind = kind,
                DisplayName = displayName,
                Relationship = string.IsNullOrWhiteSpace(relationship) ? null : relationship.Trim(),
                Aliases = aliases.Count == 0 ? null : string.Join(';', aliases),
                ProfileJson = profileJson,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParsePersonalityDocument(string? documentJson, out PersonalityProfile profile, out string error)
    {
        profile = new PersonalityProfile();
        error = "";

        if (string.IsNullOrWhiteSpace(documentJson))
        {
            error = "DocumentJson is required.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(documentJson, EditableDocumentReadOptions);
            var validator = new PersonalityProfileValidator();
            var validation = validator.ValidateJson(doc.RootElement);
            if (!validation.IsValid)
            {
                error = $"{validation.ReasonCode}: {validation.Detail}";
                return false;
            }

            profile = PersonalityProfileProjection.FromJson(doc.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ExtractPreferredName(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            if (doc.RootElement.TryGetProperty("preferred_name", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ReadRequiredString(JsonElement root, string propertyName, out string error)
    {
        error = "";
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            error = $"Profile document field '{propertyName}' is required.";
            return "";
        }

        var text = value.GetString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = $"Profile document field '{propertyName}' is required.";
            return "";
        }

        return text;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static List<string> ReadAliases(JsonElement root)
    {
        if (!root.TryGetProperty("aliases", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    private static IEnumerable<string> SplitAliases(string? aliases)
    {
        return string.IsNullOrWhiteSpace(aliases)
            ? []
            : aliases.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json, EditableDocumentReadOptions);
        return doc.RootElement.Clone();
    }

    private sealed record EditableProfileCardDocument
    {
        [JsonPropertyName("profile_id")]
        public string ProfileId { get; init; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "user";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; init; } = "";

        [JsonPropertyName("relationship")]
        public string? Relationship { get; init; }

        [JsonPropertyName("aliases")]
        public IReadOnlyList<string> Aliases { get; init; } = [];

        [JsonPropertyName("data")]
        public JsonElement Data { get; init; }
    }
}
