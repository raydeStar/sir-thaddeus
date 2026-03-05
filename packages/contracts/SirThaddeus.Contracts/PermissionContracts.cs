namespace SirThaddeus.Contracts;

public sealed record PermissionDecisionRequest(
    bool Approved);

public sealed record PermissionDecisionResponse(
    string RequestId,
    bool Applied);
