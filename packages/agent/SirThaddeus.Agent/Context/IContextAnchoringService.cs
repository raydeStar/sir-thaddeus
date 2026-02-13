using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Context;

public abstract record ContextPatch;

public sealed record PlaceContextPatch(
    string PlaceName,
    string CountryCode,
    string? RegionCode,
    double? Latitude,
    double? Longitude,
    bool LocationInferred,
    bool GeocodeMismatch,
    bool ExplicitLocationChange) : ContextPatch;

public sealed record UtilityContextPatch(string ContextKey) : ContextPatch;

public interface IContextAnchoringService
{
    string ApplyPlaceContextIfHelpful(string userMessage);

    UtilityRouter.UtilityResult? TryHandleUtilityFollowUpWithContext(string userMessage);

    UtilityContextPatch? TryBuildUtilityPatch(UtilityRouter.UtilityResult utilityResult);

    PlaceContextPatch CreatePlacePatch(
        string placeName,
        string countryCode,
        string? regionCode = null,
        double? latitude = null,
        double? longitude = null,
        bool locationInferred = false,
        bool geocodeMismatch = false,
        bool explicitLocationChange = true);

    void ApplyPatch(ContextPatch patch);

    AgentResponse AddLocationInferenceDisclosure(
        AgentResponse response,
        ValidatedSlots? validatedSlots);
}
