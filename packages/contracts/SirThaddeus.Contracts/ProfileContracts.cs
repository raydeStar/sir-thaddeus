namespace SirThaddeus.Contracts;

public sealed record ProfileSummaryResponse(
    string? ActiveProfileId,
    IReadOnlyList<ProfileListItemDto> Profiles,
    IReadOnlyList<PersonalityListItemDto> Personalities,
    string ActivePersonalityId);

public sealed record ProfileListItemDto(
    string ProfileId,
    string Kind,
    string DisplayName,
    string? PreferredName,
    string? Relationship,
    bool IsActive,
    DateTimeOffset UpdatedAtUtc);

public sealed record PersonalityListItemDto(
    string Id,
    string DisplayName,
    string Alias,
    string Description,
    bool IsActive);

public sealed record SetActiveProfileRequest(string ProfileId);

public sealed record SetActiveProfileResponse(
    bool Applied,
    string ActiveProfileId,
    string Message);

public sealed record SetActivePersonalityRequest(string PersonalityId);

public sealed record SetActivePersonalityResponse(
    bool Applied,
    string ActivePersonalityId,
    string Message);
