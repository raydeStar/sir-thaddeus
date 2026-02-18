namespace SirThaddeus.PersonalityEngine.Profiles;

public enum PersonalityValidationReasonCode
{
    None = 0,
    InvalidSchema = 1,
    OutOfRange = 2,
    DisallowedField = 3,
    UnsafeRuleAttempt = 4,
    JsonParseError = 5
}

public sealed record PersonalityValidationResult
{
    public bool IsValid { get; init; }
    public PersonalityValidationReasonCode ReasonCode { get; init; }
    public string Detail { get; init; } = "";

    public static PersonalityValidationResult Ok() => new()
    {
        IsValid = true,
        ReasonCode = PersonalityValidationReasonCode.None
    };

    public static PersonalityValidationResult Fail(PersonalityValidationReasonCode code, string detail) => new()
    {
        IsValid = false,
        ReasonCode = code,
        Detail = detail ?? ""
    };
}
