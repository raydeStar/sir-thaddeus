namespace SirThaddeus.Contracts;

public sealed record PermissionDecisionRequest(
    bool Approved,
    bool RememberForSession = false,
    bool PersistAsAlways = false);

public sealed record PermissionDecisionResponse(
    string RequestId,
    bool Applied);
