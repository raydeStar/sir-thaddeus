export class PermissionManager {
    eventBus;
    grants = new Map();
    manifests = new Map();
    constructor(eventBus) {
        this.eventBus = eventBus;
    }
    registerManifest(manifest) {
        this.manifests.set(manifest.id, manifest);
        if (!this.grants.has(manifest.id)) {
            this.grants.set(manifest.id, { moduleId: manifest.id, status: "pending" });
        }
    }
    removeManifest(moduleId) {
        this.manifests.delete(moduleId);
        this.grants.delete(moduleId);
    }
    listRequiredPermissions(moduleId) {
        return this.requireManifest(moduleId).permissions;
    }
    listGrants() {
        return [...this.grants.values()];
    }
    getGrant(moduleId) {
        return this.grants.get(moduleId) ?? { moduleId, status: "pending" };
    }
    async grant(moduleId) {
        this.requireManifest(moduleId);
        const grant = { moduleId, status: "granted", grantedAt: new Date() };
        this.grants.set(moduleId, grant);
        await this.eventBus?.publish("module.permissions_granted", { grant }, { moduleId });
        return grant;
    }
    async deny(moduleId, reason) {
        this.requireManifest(moduleId);
        const grant = { moduleId, status: "denied", reason, deniedAt: new Date() };
        this.grants.set(moduleId, grant);
        await this.eventBus?.publish("module.permissions_denied", { grant }, { moduleId });
        return grant;
    }
    async reset(moduleId) {
        this.requireManifest(moduleId);
        const grant = { moduleId, status: "pending" };
        this.grants.set(moduleId, grant);
        await this.eventBus?.publish("module.permissions_reset", { grant }, { moduleId });
        return grant;
    }
    canUseModule(moduleId) {
        this.requireManifest(moduleId);
        const grant = this.getGrant(moduleId);
        if (grant.status === "granted") {
            return { allowed: true };
        }
        return {
            allowed: false,
            reason: grant.status === "denied"
                ? grant.reason ?? `Permissions denied for module '${moduleId}'.`
                : `Permissions pending for module '${moduleId}'.`
        };
    }
    canAccessMemory(moduleId, namespace, mode) {
        const moduleDecision = this.canUseModule(moduleId);
        if (!moduleDecision.allowed) {
            return moduleDecision;
        }
        const manifest = this.requireManifest(moduleId);
        const allowedNamespaces = manifest.permissions.memory[mode];
        if (!allowedNamespaces.includes(namespace)) {
            return {
                allowed: false,
                reason: `Module '${moduleId}' did not request ${mode} access to memory namespace '${namespace}'.`
            };
        }
        return { allowed: true };
    }
    requireManifest(moduleId) {
        const manifest = this.manifests.get(moduleId);
        if (!manifest) {
            throw new Error(`Module '${moduleId}' is not registered for permissions.`);
        }
        return manifest;
    }
}
