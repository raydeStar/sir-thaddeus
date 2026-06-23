import { EventBus } from "./EventBus.js";
import {
  CapabilityName,
  ModuleId,
  ModuleManifest,
  NormalizedModuleManifest,
  normalizeModuleManifest
} from "./ModuleManifest.js";

export interface RegisteredCapability {
  moduleId: ModuleId;
  name: CapabilityName;
  kind: "tool" | "job" | "hook";
}

export class ModuleRegistry {
  private readonly modules = new Map<ModuleId, NormalizedModuleManifest>();

  constructor(private readonly eventBus?: EventBus) {}

  async register(manifest: ModuleManifest): Promise<NormalizedModuleManifest> {
    const normalized = normalizeModuleManifest(manifest);
    if (this.modules.has(normalized.id)) {
      throw new Error(`Module '${normalized.id}' is already registered.`);
    }

    this.assertNoCapabilityCollisions(normalized);
    this.modules.set(normalized.id, normalized);
    await this.eventBus?.publish("module.registered", { manifest: normalized }, { moduleId: normalized.id });
    return normalized;
  }

  async unregister(moduleId: ModuleId): Promise<boolean> {
    const manifest = this.modules.get(moduleId);
    if (!manifest) {
      return false;
    }

    this.modules.delete(moduleId);
    await this.eventBus?.publish("module.unregistered", { manifest }, { moduleId });
    return true;
  }

  get(moduleId: ModuleId): NormalizedModuleManifest | undefined {
    return this.modules.get(moduleId);
  }

  list(): NormalizedModuleManifest[] {
    return [...this.modules.values()];
  }

  getCapability(name: CapabilityName): RegisteredCapability | undefined {
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

  listCapabilities(moduleId?: ModuleId): RegisteredCapability[] {
    const manifests = moduleId ? [this.require(moduleId)] : this.list();
    return manifests.flatMap((manifest) => [
      ...manifest.tools.map((name) => ({ moduleId: manifest.id, name, kind: "tool" as const })),
      ...manifest.jobs.map((name) => ({ moduleId: manifest.id, name, kind: "job" as const })),
      ...manifest.hooks.map((name) => ({ moduleId: manifest.id, name, kind: "hook" as const }))
    ]);
  }

  require(moduleId: ModuleId): NormalizedModuleManifest {
    const manifest = this.modules.get(moduleId);
    if (!manifest) {
      throw new Error(`Module '${moduleId}' is not registered.`);
    }
    return manifest;
  }

  private assertNoCapabilityCollisions(manifest: NormalizedModuleManifest): void {
    for (const capability of [...manifest.tools, ...manifest.jobs, ...manifest.hooks]) {
      const existing = this.getCapability(capability);
      if (existing) {
        throw new Error(
          `Capability '${capability}' is already registered by module '${existing.moduleId}'.`
        );
      }
    }
  }
}
