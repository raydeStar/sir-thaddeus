using System.Diagnostics;

namespace SirThaddeus.UI.Avalonia;

internal sealed class LocalTextToSpeechPlaybackService
{
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

        const string script = "$text = [Console]::In.ReadToEnd(); Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Speak($text);";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"{script}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start local speech playback process.");
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
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Best effort cleanup only.
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(error))
            {
                error = "Unknown speech playback error.";
            }

            throw new InvalidOperationException(error.Trim());
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
