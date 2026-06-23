import { normalizeModuleManifest } from "./ModuleManifest.js";
export class ModuleRegistry {
    eventBus;
    modules = new Map();
    constructor(eventBus) {
        this.eventBus = eventBus;
    }
    async register(manifest) {
        const normalized = normalizeModuleManifest(manifest);
        if (this.modules.has(normalized.id)) {
            throw new Error(`Module '${normalized.id}' is already registered.`);
        }
        this.assertNoCapabilityCollisions(normalized);
        this.modules.set(normalized.id, normalized);
        await this.eventBus?.publish("module.registered", { manifest: normalized }, { moduleId: normalized.id });
        return normalized;
    }
    async unregister(moduleId) {
        const manifest = this.modules.get(moduleId);
        if (!manifest) {
            return false;
        }
        this.modules.delete(moduleId);
        await this.eventBus?.publish("module.unregistered", { manifest }, { moduleId });
        return true;
    }
    get(moduleId) {
        return this.modules.get(moduleId);
    }
    list() {
        return [...this.modules.values()];
    }
    getCapability(name) {
        for (const manifest of this.modules.values()) {
            if (manifest.tools.includes(name)) {
                return { moduleId: manifest.id, name, kind: "tool" };
            }
            if (manifest.jobs.includes(name)) {
                return { moduleId: manifest.id, name, kind: "job" };
            }
            if (manifest.hooks.includes(name)) {
                return { moduleId: manifest.id, name, kind: "hook" };
            }
        }
        return undefined;
    }
    listCapabilities(moduleId) {
        const manifests = moduleId ? [this.require(moduleId)] : this.list();
        return manifests.flatMap((manifest) => [
            ...manifest.tools.map((name) => ({ moduleId: manifest.id, name, kind: "tool" })),
            ...manifest.jobs.map((name) => ({ moduleId: manifest.id, name, kind: "job" })),
            ...manifest.hooks.map((name) => ({ moduleId: manifest.id, name, kind: "hook" }))
        ]);
    }
    require(moduleId) {
        const manifest = this.modules.get(moduleId);
        if (!manifest) {
            throw new Error(`Module '${moduleId}' is not registered.`);
        }
        return manifest;
    }
    assertNoCapabilityCollisions(manifest) {
        for (const capability of [...manifest.tools, ...manifest.jobs, ...manifest.hooks]) {
            const existing = this.getCapability(capability);
            if (existing) {
                throw new Error(`Capability '${capability}' is already registered by module '${existing.moduleId}'.`);
            }
        }
    }
}
