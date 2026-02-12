namespace SirThaddeus.VoiceHost.Backends;

public sealed class BackendProxyException : Exception
{
    public BackendProxyException(string message)
        : base(message)
    {
    }
}
