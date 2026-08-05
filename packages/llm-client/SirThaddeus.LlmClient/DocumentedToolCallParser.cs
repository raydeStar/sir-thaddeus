using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SirThaddeus.LlmClient;

/// <summary>
/// Strict fallback for providers that return the documented Liquid Pythonic
/// tool-call wire format instead of OpenAI <c>tool_calls</c>. This is a parser,
/// never an evaluator: it accepts only literals and advertised function names.
/// </summary>
internal static class DocumentedToolCallParser
{
    internal const string StartToken = "<|tool_call_start|>";
    internal const string EndToken = "<|tool_call_end|>";
    private const int MaxSourceLength = 65_536;
    private const int MaxCalls = 8;

    internal static IReadOnlyList<ToolCallRequest>? TryParse(
        IReadOnlyList<string?> sources,
        IReadOnlyList<ToolDefinition>? advertisedTools)
    {
        if (advertisedTools is not { Count: > 0 })
            return null;

        var allowedNames = advertisedTools
            .Select(tool => tool.Function.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        if (allowedNames.Count == 0)
            return null;

        string? block = null;
        foreach (var source in sources.Where(value => !string.IsNullOrEmpty(value)))
        {
            if (source!.Length > MaxSourceLength)
                return null;

            var starts = CountOccurrences(source, StartToken);
            var ends = CountOccurrences(source, EndToken);
            if (starts == 0 && ends == 0)
                continue;
            if (starts != 1 || ends != 1 || block is not null)
                return null;

            var start = source.IndexOf(StartToken, StringComparison.Ordinal) + StartToken.Length;
            var end = source.IndexOf(EndToken, start, StringComparison.Ordinal);
            if (end < start)
                return null;
            block = source[start..end];
        }

        if (block is null)
            return null;

        try
        {
            var parser = new LiteralParser(block, allowedNames);
            return parser.ParseCalls();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string? RemoveBalancedBlock(string? content)
    {
        if (string.IsNullOrEmpty(content)
            || CountOccurrences(content, StartToken) != 1
            || CountOccurrences(content, EndToken) != 1)
            return content;

        var start = content.IndexOf(StartToken, StringComparison.Ordinal);
        var end = content.IndexOf(EndToken, start + StartToken.Length, StringComparison.Ordinal);
        if (end < 0)
            return content;
        return (content[..start] + content[(end + EndToken.Length)..]).Trim();
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    private sealed class LiteralParser(string input, HashSet<string> allowedNames)
    {
        private int _index;
        private int _argumentCount;

        internal IReadOnlyList<ToolCallRequest> ParseCalls()
        {
            SkipWhitespace();
            Expect('[');
            SkipWhitespace();
            var calls = new List<ToolCallRequest>();
            if (TryConsume(']'))
                throw Error();

            while (true)
            {
                if (calls.Count >= MaxCalls)
                    throw Error();
                calls.Add(ParseCall(calls.Count));
                SkipWhitespace();
                if (TryConsume(']'))
                    break;
                Expect(',');
                SkipWhitespace();
                if (Peek() == ']')
                    throw Error();
            }

            SkipWhitespace();
            if (_index != input.Length)
                throw Error();
            return calls;
        }

        private ToolCallRequest ParseCall(int callIndex)
        {
            var name = ParseIdentifier();
            if (!allowedNames.Contains(name))
                throw Error();
            SkipWhitespace();
            Expect('(');
            SkipWhitespace();
            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!TryConsume(')'))
            {
                while (true)
                {
                    if (++_argumentCount > 128)
                        throw Error();
                    var key = ParseIdentifier();
                    SkipWhitespace();
                    Expect('=');
                    SkipWhitespace();
                    if (!arguments.TryAdd(key, ParseValue(0)))
                        throw Error();
                    SkipWhitespace();
                    if (TryConsume(')'))
                        break;
                    Expect(',');
                    SkipWhitespace();
                    if (Peek() == ')')
                        throw Error();
                }
            }

            return new ToolCallRequest
            {
                Id = $"call_documented_{callIndex + 1}",
                Function = new FunctionCallDetails
                {
                    Name = name,
                    Arguments = JsonSerializer.Serialize(arguments)
                }
            };
        }

        private object? ParseValue(int depth)
        {
            if (depth > 16)
                throw Error();
            SkipWhitespace();
            return Peek() switch
            {
                '\'' or '"' => ParseString(),
                '[' => ParseList(depth + 1),
                '{' => ParseObject(depth + 1),
                '-' or >= '0' and <= '9' => ParseNumber(),
                _ => ParseLiteral()
            };
        }

        private List<object?> ParseList(int depth)
        {
            Expect('[');
            SkipWhitespace();
            var values = new List<object?>();
            if (TryConsume(']'))
                return values;
            while (true)
            {
                values.Add(ParseValue(depth));
                SkipWhitespace();
                if (TryConsume(']'))
                    return values;
                Expect(',');
                SkipWhitespace();
                if (Peek() == ']')
                    throw Error();
            }
        }

        private Dictionary<string, object?> ParseObject(int depth)
        {
            Expect('{');
            SkipWhitespace();
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (TryConsume('}'))
                return values;
            while (true)
            {
                if (Peek() is not ('\'' or '"'))
                    throw Error();
                var key = ParseString();
                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                if (!values.TryAdd(key, ParseValue(depth)))
                    throw Error();
                SkipWhitespace();
                if (TryConsume('}'))
                    return values;
                Expect(',');
                SkipWhitespace();
                if (Peek() == '}')
                    throw Error();
            }
        }

        private object ParseNumber()
        {
            var start = _index;
            if (Peek() == '-')
                _index++;
            ConsumeDigits(required: true);
            if (Peek() == '.')
            {
                _index++;
                ConsumeDigits(required: true);
            }
            if (Peek() is 'e' or 'E')
            {
                _index++;
                if (Peek() is '+' or '-')
                    _index++;
                ConsumeDigits(required: true);
            }
            var token = input[start.._index];
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                return integer;
            if (decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
            throw Error();
        }

        private object? ParseLiteral()
        {
            var value = ParseIdentifier();
            return value switch
            {
                "True" or "true" => true,
                "False" or "false" => false,
                "None" or "null" => null,
                _ => throw Error()
            };
        }

        private string ParseString()
        {
            var quote = Peek();
            if (quote is not ('\'' or '"'))
                throw Error();
            _index++;
            var result = new StringBuilder();
            while (_index < input.Length)
            {
                var current = input[_index++];
                if (current == quote)
                    return result.ToString();
                if (current != '\\')
                {
                    if (char.IsControl(current))
                        throw Error();
                    result.Append(current);
                    continue;
                }
                if (_index >= input.Length)
                    throw Error();
                var escaped = input[_index++];
                result.Append(escaped switch
                {
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => ParseUnicodeEscape(),
                    _ => throw Error()
                });
            }
            throw Error();
        }

        private char ParseUnicodeEscape()
        {
            if (_index + 4 > input.Length)
                throw Error();
            var token = input.AsSpan(_index, 4);
            if (!ushort.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                throw Error();
            _index += 4;
            return (char)value;
        }

        private string ParseIdentifier()
        {
            SkipWhitespace();
            var start = _index;
            if (_index >= input.Length || !(char.IsLetter(input[_index]) || input[_index] == '_'))
                throw Error();
            _index++;
            while (_index < input.Length
                   && (char.IsLetterOrDigit(input[_index]) || input[_index] == '_'))
                _index++;
            return input[start.._index];
        }

        private void ConsumeDigits(bool required)
        {
            var start = _index;
            while (_index < input.Length && char.IsAsciiDigit(input[_index]))
                _index++;
            if (required && start == _index)
                throw Error();
        }

        private char Peek() => _index < input.Length ? input[_index] : '\0';

        private void SkipWhitespace()
        {
            while (_index < input.Length && char.IsWhiteSpace(input[_index]))
                _index++;
        }

        private void Expect(char expected)
        {
            if (!TryConsume(expected))
                throw Error();
        }

        private bool TryConsume(char expected)
        {
            if (Peek() != expected)
                return false;
            _index++;
            return true;
        }

        private static FormatException Error() => new("Invalid documented tool call.");
    }
}
