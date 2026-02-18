using System.Reflection;
using System.Text;

namespace SirThaddeus.PersonalityEngine.Profiles;

public static class BuiltInProfileCatalog
{
    public const string HelpfulDefaultId = "helpful_default";
    public const string ProfessionalId = "professional";
    public const string SirThaddeusId = "sir_thaddeus";

    public static readonly IReadOnlyList<string> BuiltInIds =
    [
        HelpfulDefaultId,
        ProfessionalId,
        SirThaddeusId
    ];

    public static IEnumerable<(string Id, string Json)> ReadAll()
    {
        foreach (var id in BuiltInIds)
            yield return (id, ReadBuiltInJson(id));
    }

    public static string ReadBuiltInJson(string id)
    {
        var assembly = typeof(BuiltInProfileCatalog).Assembly;
        var resourceName = GetResourceName(id);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Built-in personality profile resource not found: {resourceName}");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string GetResourceName(string id) =>
        $"SirThaddeus.PersonalityEngine.Profiles.BuiltIns.{id}.json";
}
