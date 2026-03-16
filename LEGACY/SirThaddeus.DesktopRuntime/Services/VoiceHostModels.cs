namespace SirThaddeus.DesktopRuntime.Services;

public sealed record VoiceHostEnsureResult
{
    public required bool Success { get; init; }
    public string? BaseUrl { get; init; }
    public string? ErrorCode { get; init; }
    public string UserMessage { get; init; } = "";

    public static VoiceHostEnsureResult Ok(string baseUrl) => new()
    {
        Success = true,
        BaseUrl = baseUrl
    };

    public static VoiceHostEnsureResult Failure(string errorCode, string message) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        UserMessage = message
    };
}

public sealed record VoiceHostHealthResult(
    bool Reachable,
    bool Ready,
    string Status,
    bool AsrReady,
    bool TtsReady,
    string Version,
    string ErrorCode,
    string Message)
{
    public static VoiceHostHealthResult Unreachable(
        string errorCode = "",
        string message = "")
        => new(false, false, "", false, false, "", errorCode, message);
}
