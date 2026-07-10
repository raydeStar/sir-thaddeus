using System.Text.RegularExpressions;

namespace SirThaddeus.Agent;

/// <summary>
/// Recognizes user-requested answer shapes without coupling behavior to one
/// fixture sentence. This is intentionally about format only; it never knows
/// or derives the answer itself.
/// </summary>
public static class StrictAnswerContract
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex CommandedNumericOnly = new(
        @"\b(?:reply|respond|answer|return|output|give(?:\s+me)?)\b.{0,32}\b(?:only|just)\b.{0,16}\b(?:(?:the|an?|one|a\s+single|final)\s+){0,2}(?:integer|number|decimal|numeric\s+value|value|remainder|count|sum|digits?)\b",
        Options);

    private static readonly Regex LeadingNumericOnly = new(
        @"\b(?:only|just)\s+(?:(?:the|an?|one|a\s+single|final)\s+){0,2}(?:integer|number|decimal|numeric\s+value|value|remainder|count|sum|digits?)\b",
        Options);

    private static readonly Regex TrailingNumericOnly = new(
        @"\b(?:integer|number|decimal|numeric\s+value|value|remainder|count|sum|digits?)\s+only\b",
        Options);

    private static readonly Regex BareNumericValue = new(
        @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool RequestsBareNumeric(string? userText) =>
        !string.IsNullOrWhiteSpace(userText) &&
        (CommandedNumericOnly.IsMatch(userText) ||
         LeadingNumericOnly.IsMatch(userText) ||
         TrailingNumericOnly.IsMatch(userText));

    public static bool IsBareNumeric(string? value) =>
        !string.IsNullOrWhiteSpace(value) && BareNumericValue.IsMatch(value.Trim());
}
