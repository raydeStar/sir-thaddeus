using System.Text;

namespace SirThaddeus.Harness.Models;

/// <summary>
/// Decodes evaluator-owned file fixtures while keeping binary payloads bounded.
/// This is test-harness infrastructure and is not used by the production file
/// tools or assistant pipeline.
/// </summary>
internal static class HarnessFileContent
{
    public const int MaxDecodedBytes = 10 * 1024 * 1024;

    private const int MaxEncodedChars = ((MaxDecodedBytes + 2) / 3) * 4;

    public static byte[] Decode(HarnessFileSetup file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.ContentBase64 is null)
        {
            var textBytes = Encoding.UTF8.GetBytes(file.Content ?? string.Empty);
            EnsureBounded(textBytes.Length);
            return textBytes;
        }

        if (!string.IsNullOrEmpty(file.Content))
        {
            throw new InvalidOperationException(
                "Harness file fixture cannot define both content and content_base64.");
        }

        if (file.ContentBase64.Length > MaxEncodedChars)
        {
            throw new InvalidOperationException(
                $"Harness file fixture exceeds the {MaxDecodedBytes:N0}-byte limit.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(file.ContentBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Harness file fixture content_base64 is not valid Base64.", ex);
        }

        EnsureBounded(bytes.Length);
        return bytes;
    }

    private static void EnsureBounded(int byteCount)
    {
        if (byteCount > MaxDecodedBytes)
        {
            throw new InvalidOperationException(
                $"Harness file fixture exceeds the {MaxDecodedBytes:N0}-byte limit.");
        }
    }
}
