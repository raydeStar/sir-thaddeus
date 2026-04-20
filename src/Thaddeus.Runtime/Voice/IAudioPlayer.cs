using System.Runtime.InteropServices;

namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Plays a synthesised WAV file through the host's audio device. Decoupled
/// from <see cref="ITextToSpeechProvider"/> so adapters can be tested without
/// hitting the speakers and so platform-specific players (Windows wave, ALSA,
/// CoreAudio) can be swapped in independently.
/// </summary>
public interface IAudioPlayer
{
    /// <summary>True when the player can play audio on the current host.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Plays the WAV file at <paramref name="wavPath"/> and completes when
    /// playback drains. Honours <paramref name="ct"/> by stopping playback
    /// promptly so stop-all stays responsive (spec §11.4).
    /// </summary>
    Task PlayWavFileAsync(string wavPath, CancellationToken ct);
}

/// <summary>
/// Default <see cref="IAudioPlayer"/>. On Windows it uses <c>winmm!PlaySound</c>
/// directly so the runtime project doesn't pull in
/// <c>System.Windows.Extensions</c>. Other platforms report unavailable in
/// Phase 2.3; native players land in Phase 7 (cross-platform).
/// </summary>
public sealed class DefaultAudioPlayer : IAudioPlayer
{
    // PlaySound flags (mmsystem.h)
    private const uint SND_SYNC = 0x00000000;
    private const uint SND_ASYNC = 0x00000001;
    private const uint SND_NODEFAULT = 0x00000002;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_PURGE = 0x00000040;

    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    /// <inheritdoc />
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc />
    public async Task PlayWavFileAsync(string wavPath, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(
                "DefaultAudioPlayer only supports Windows. A platform player will arrive in Phase 7.");
        }
        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException("WAV file not found.", wavPath);
        }

        // Use SND_SYNC on a background thread so cancellation can interrupt by
        // calling PlaySound(null, 0, SND_PURGE), which stops the current sound.
        await using var registration = ct.Register(() =>
        {
            try { PlaySound(null, IntPtr.Zero, SND_PURGE); } catch { /* best effort */ }
        });

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            PlaySound(wavPath, IntPtr.Zero, SND_FILENAME | SND_SYNC | SND_NODEFAULT);
        }, CancellationToken.None).ConfigureAwait(false);
    }
}

