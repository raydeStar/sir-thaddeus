using System.Diagnostics;
using NAudio.Wave;

namespace SirThaddeus.UI.Avalonia;

internal sealed class LocalTextToSpeechPlaybackService
{
    public int OutputDeviceNumber { get; set; } = -1;

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Read aloud is currently implemented for Windows only.");
        }

        var tempWavPath = Path.Combine(Path.GetTempPath(), $"sir-thaddeus-tts-{Guid.NewGuid():N}.wav");
        try
        {
            await RenderSpeechAsync(text, tempWavPath, cancellationToken);
            await PlayWaveFileAsync(tempWavPath, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempWavPath))
                {
                    File.Delete(tempWavPath);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static async Task RenderSpeechAsync(string text, string outputPath, CancellationToken cancellationToken)
    {
        const string script = "$text = [Console]::In.ReadToEnd(); $path = $args[0]; Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; try { $s.SetOutputToWaveFile($path); $s.Speak($text); } finally { $s.Dispose(); }";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"{script}\" \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start local speech rendering process.");
        }

        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            TryKillProcess((Process)state!);
        }, process);

        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Unknown speech rendering error." : error.Trim());
        }
    }

    private async Task PlayWaveFileAsync(string wavePath, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(wavePath);
        using var output = new WaveOutEvent { DeviceNumber = OutputDeviceNumber };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                completion.TrySetException(args.Exception);
            }
            else
            {
                completion.TrySetResult();
            }
        };

        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            try
            {
                ((WaveOutEvent)state!).Stop();
            }
            catch
            {
                // Best effort cancellation only.
            }
        }, output);

        output.Init(reader);
        output.Play();

        try
        {
            await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
