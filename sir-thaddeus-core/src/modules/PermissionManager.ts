import { EventBus } from "./EventBus.js";
import {
  MemoryNamespace,
  ModuleId,
  NormalizedModuleManifest
} from "./ModuleManifest.js";

export type PermissionGrantStatus = "pending" | "granted" | "denied";
export type MemoryAccessMode = "read" | "write";

export interface PermissionGrant {
  moduleId: ModuleId;
  status: PermissionGrantStatus;
  reason?: string;
  grantedAt?: Date;
  deniedAt?: Date;
}

export interface PermissionDecision {
  allowed: boolean;
  reason?: string;
}

export class PermissionManager {
  private readonly grants = new Map<ModuleId, PermissionGrant>();
  private readonly manifests = new Map<ModuleId, NormalizedModuleManifest>();

  constructor(private readonly eventBus?: EventBus) {}

  registerManifest(manifest: NormalizedModuleManifest): void {
    this.manifests.set(manifest.id, manifest);
    if (!this.grants.has(manifest.id)) {
      this.grants.set(manifest.id, { moduleId: manifest.id, status: "pending" });
    }
  }

  removeManifest(moduleId: ModuleId): void {
    this.manifests.delete(moduleId);
    this.grants.delete(moduleId);
  }

  listRequiredPermissions(moduleId: ModuleId) {
    return this.requireManifest(moduleId).permissions;
  }

  listGrants(): PermissionGrant[] {
    return [...this.grants.values()];
  }

  getGrant(moduleId: ModuleId): PermissionGrant {
    return this.grants.get(moduleId) ?? { moduleId, status: "pending" };
  }

  async grant(moduleId: ModuleId): Promise<PermissionGrant> {
    this.requireManifest(moduleId);
    const grant: PermissionGrant = { moduleId, status: "granted", grantedAt: new Date() };
    this.grants.set(moduleId, grant);
    await this.eventBus?.publish("module.permissions_granted", { grant }, { moduleId });
    return grant;
  }

  async deny(moduleId: ModuleId, reason?: string): Promise<PermissionGrant> {
    this.requireManifest(moduleId);
    const grant: PermissionGrant = { moduleId, status: "denied", reason, deniedAt: new Date() };
    this.grants.set(moduleId, grant);
    await this.eventBus?.publish("module.permissions_denied", { grant }, { moduleId });
    return grant;
  }

  async reset(moduleId: ModuleId): Promise<PermissionGrant> {
    this.requireManifest(moduleId);
    const grant: PermissionGrant = { moduleId, status: "pending" };
    this.grants.set(moduleId, grant);
    await this.eventBus?.publish("module.permissions_reset", { grant }, { moduleId });
    return grant;
  }

  canUseModule(moduleId: ModuleId): PermissionDecision {
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

  canAccessMemory(moduleId: ModuleId, namespace: MemoryNamespace, mode: MemoryAccessMode): PermissionDecision {
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

  private requireManifest(moduleId: ModuleId): NormalizedModuleManifest {
    const manifest = this.manifests.get(moduleId);
    if (!manifest) {
      throw new Error(`Module '${moduleId}' is not registered for permissions.`);
    }
    return manifest;
  }
}
