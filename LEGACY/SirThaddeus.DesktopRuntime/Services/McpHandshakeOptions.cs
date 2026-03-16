using SirThaddeus.McpShared;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Startup compatibility checks for MCP tool-server handshake.
/// </summary>
public sealed record McpHandshakeOptions
{
    public bool Strict { get; init; } = true;
    public string RequiredProtocolVersion { get; init; } = McpContract.ProtocolVersion;
    public string RequiredServerContractVersion { get; init; } = McpContract.ServerContractVersion;
    public string RequiredManifestHashSha256 { get; init; } = "";
}
