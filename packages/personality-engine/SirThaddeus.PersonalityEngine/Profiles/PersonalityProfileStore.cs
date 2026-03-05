using System.Text;
using System.Text.Json;

namespace SirThaddeus.PersonalityEngine.Profiles;

public sealed record PersonalityLoadResult
{
    public required PersonalityProfile Profile { get; init; }
    public required string Hash { get; init; }
    public required string SourcePath { get; init; }
    public bool FellBackToDefault { get; init; }
    public PersonalityValidationReasonCode ReasonCode { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// File-backed store for personality profiles.
/// </summary>
public sealed class PersonalityProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonDocumentOptions JsonDocumentReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly PersonalityProfileValidator _validator;

    public PersonalityProfileStore(PersonalityProfileValidator? validator = null)
    {
        _validator = validator ?? new PersonalityProfileValidator();
    }

    public string EnsureProfileDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Profile directory is required.", nameof(directory));

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        return directory;
    }

    public void EnsureBuiltInsInstalled(string profilesDirectory)
    {
        var dir = EnsureProfileDirectory(profilesDirectory);
        foreach (var (id, json) in BuiltInProfileCatalog.ReadAll())
        {
            var path = ResolveProfilePath(dir, id);
            if (File.Exists(path))
                continue;

            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(json);
            }
            catch (IOException)
            {
                // Concurrent test runs can race on built-in provisioning.
                // If another writer created the file first, this is fine.
                if (!File.Exists(path))
                    throw;
            }
        }
    }

    public IReadOnlyList<PersonalityProfileDescriptor> ListProfiles(string profilesDirectory)
    {
        var dir = EnsureProfileDirectory(profilesDirectory);
        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var output = new List<PersonalityProfileDescriptor>(files.Count);
        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(text, JsonDocumentReadOptions);
                var validation = _validator.ValidateJson(doc.RootElement);
                if (!validation.IsValid)
                    continue;

                var profile = PersonalityProfileProjection.FromJson(doc.RootElement, JsonOptions);

                output.Add(new PersonalityProfileDescriptor
                {
                    Id = profile.Id,
                    DisplayName = profile.DisplayName,
                    Alias = profile.Alias,
                    Description = profile.Description,
                    Hash = CanonicalJsonHasher.ComputeHash(doc.RootElement),
                    SourcePath = file
                });
            }
            catch
            {
                // Keep listing resilient; malformed files are handled at load time.
            }
        }

        return output;
    }

    public PersonalityLoadResult LoadActive(
        string profilesDirectory,
        string? activeProfileId)
    {
        var requested = string.IsNullOrWhiteSpace(activeProfileId)
            ? BuiltInProfileCatalog.HelpfulDefaultId
            : activeProfileId.Trim().ToLowerInvariant();

        var loaded = TryLoadById(profilesDirectory, requested);
        if (loaded is not null && !loaded.FellBackToDefault)
            return loaded;

        var fallback = TryLoadById(profilesDirectory, BuiltInProfileCatalog.HelpfulDefaultId);
        if (fallback is not null)
        {
            var reason = loaded?.ReasonCode ?? PersonalityValidationReasonCode.InvalidSchema;
            var detail = loaded?.Detail;
            if (string.IsNullOrWhiteSpace(detail))
                detail = $"Requested profile '{requested}' is missing or invalid.";

            return fallback with
            {
                FellBackToDefault = true,
                ReasonCode = reason,
                Detail = detail
            };
        }

        // Should not happen if built-ins are provisioned, but keep runtime alive.
        var emergency = new PersonalityProfile
        {
            Id = BuiltInProfileCatalog.HelpfulDefaultId,
            DisplayName = "Helpful Default",
            Description = "Fallback profile."
        };

        return new PersonalityLoadResult
        {
            Profile = emergency,
            Hash = CanonicalJsonHasher.ComputeHash(emergency),
            SourcePath = "<memory>",
            FellBackToDefault = true,
            ReasonCode = PersonalityValidationReasonCode.InvalidSchema,
            Detail = "No valid profile files were available."
        };
    }

    public PersonalityProfileDescriptor SaveProfile(string profilesDirectory, PersonalityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var validation = _validator.ValidateProfile(profile);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Profile validation failed ({validation.ReasonCode}): {validation.Detail}");

        var dir = EnsureProfileDirectory(profilesDirectory);
        var path = ResolveProfilePath(dir, profile.Id);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);

        return new PersonalityProfileDescriptor
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            Alias = profile.Alias,
            Description = profile.Description,
            Hash = CanonicalJsonHasher.ComputeHash(profile),
            SourcePath = path
        };
    }

    public PersonalityProfileDescriptor SaveProfileTemplate(
        string profilesDirectory,
        PersonalityProfile profile,
        string templateJsonWithComments)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(templateJsonWithComments))
            throw new ArgumentException("Template JSON is required.", nameof(templateJsonWithComments));

        var profileValidation = _validator.ValidateProfile(profile);
        if (!profileValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"Profile validation failed ({profileValidation.ReasonCode}): {profileValidation.Detail}");
        }

        using var doc = JsonDocument.Parse(templateJsonWithComments, JsonDocumentReadOptions);
        var jsonValidation = _validator.ValidateJson(doc.RootElement);
        if (!jsonValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"Template validation failed ({jsonValidation.ReasonCode}): {jsonValidation.Detail}");
        }

        var parsedProfile = PersonalityProfileProjection.FromJson(doc.RootElement, JsonOptions);

        if (!string.Equals(parsedProfile.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Template id '{parsedProfile.Id}' did not match expected id '{profile.Id}'.");
        }

        var dir = EnsureProfileDirectory(profilesDirectory);
        var path = ResolveProfilePath(dir, profile.Id);
        File.WriteAllText(path, templateJsonWithComments, Encoding.UTF8);

        return new PersonalityProfileDescriptor
        {
            Id = parsedProfile.Id,
            DisplayName = parsedProfile.DisplayName,
            Alias = parsedProfile.Alias,
            Description = parsedProfile.Description,
            Hash = CanonicalJsonHasher.ComputeHash(doc.RootElement),
            SourcePath = path
        };
    }

    public PersonalityProfileDescriptor DuplicateProfile(
        string profilesDirectory,
        string sourceProfileId,
        string newProfileId)
    {
        var source = LoadActive(profilesDirectory, sourceProfileId).Profile;
        var clone = source with
        {
            Id = newProfileId.Trim().ToLowerInvariant(),
            DisplayName = $"{source.DisplayName} (Copy)"
        };
        return SaveProfile(profilesDirectory, clone);
    }

    public string ResolveProfilePath(string profilesDirectory, string profileId)
    {
        var id = (profileId ?? "").Trim().ToLowerInvariant();
        return Path.Combine(profilesDirectory, $"{id}.json");
    }

    private PersonalityLoadResult? TryLoadById(string profilesDirectory, string profileId)
    {
        var dir = EnsureProfileDirectory(profilesDirectory);
        var path = ResolveProfilePath(dir, profileId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json, JsonDocumentReadOptions);

            var validation = _validator.ValidateJson(doc.RootElement);
            if (!validation.IsValid)
            {
                return new PersonalityLoadResult
                {
                    Profile = new PersonalityProfile
                    {
                        Id = profileId,
                        DisplayName = profileId,
                        Description = "Invalid profile placeholder."
                    },
                    Hash = "invalid",
                    SourcePath = path,
                    FellBackToDefault = true,
                    ReasonCode = validation.ReasonCode,
                    Detail = validation.Detail
                };
            }

            var profile = PersonalityProfileProjection.FromJson(doc.RootElement, JsonOptions);

            // Hard invariant: permissions are never overridable by personality. Force true regardless of file content.
            profile = profile with { BehaviorRules = profile.BehaviorRules with { NeverOverridePermissions = true } };

            return new PersonalityLoadResult
            {
                Profile = profile,
                Hash = CanonicalJsonHasher.ComputeHash(doc.RootElement),
                SourcePath = path,
                FellBackToDefault = false,
                ReasonCode = PersonalityValidationReasonCode.None
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PersonalityLoadResult
            {
                Profile = new PersonalityProfile
                {
                    Id = profileId,
                    DisplayName = profileId,
                    Description = "Invalid profile placeholder."
                },
                Hash = "invalid",
                SourcePath = path,
                FellBackToDefault = true,
                ReasonCode = ex is JsonException
                    ? PersonalityValidationReasonCode.JsonParseError
                    : PersonalityValidationReasonCode.InvalidSchema,
                Detail = ex.Message
            };
        }
    }
}
