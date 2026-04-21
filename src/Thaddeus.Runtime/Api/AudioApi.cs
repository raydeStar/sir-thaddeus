using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Thaddeus.Runtime.Audio;

namespace Thaddeus.Runtime.Api;

/// <summary>Routes for discovering host audio devices.</summary>
public static class AudioApi
{
    public static IEndpointRouteBuilder MapAudioApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audio/devices", () =>
        {
            var response = new AudioDevicesResponse(
                Inputs: AudioDeviceEnumerator.GetInputDevices(),
                Outputs: AudioDeviceEnumerator.GetOutputDevices());
            return Results.Json(response, AudioJsonContext.Default.AudioDevicesResponse);
        });

        return app;
    }
}

public sealed record AudioDevicesResponse(
    IReadOnlyList<AudioDeviceInfo> Inputs,
    IReadOnlyList<AudioDeviceInfo> Outputs);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AudioDevicesResponse))]
[JsonSerializable(typeof(AudioDeviceInfo))]
public partial class AudioJsonContext : JsonSerializerContext
{
}
