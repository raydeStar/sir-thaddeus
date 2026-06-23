import { EventBus } from "../modules/EventBus.js";
import { MemoryNamespace, ModuleId } from "../modules/ModuleManifest.js";
import { PermissionManager } from "../modules/PermissionManager.js";

export interface MemoryRecord<TValue = unknown> {
  id: string;
  namespace: MemoryNamespace;
  value: TValue;
  createdBy: ModuleId;
  createdAt: Date;
  updatedAt: Date;
  metadata?: Record<string, unknown>;
}

export interface MemoryWrite<TValue = unknown> {
  id?: string;
  namespace: MemoryNamespace;
  value: TValue;
  metadata?: Record<string, unknown>;
}

export class MemoryService {
  private readonly namespaces = new Set<MemoryNamespace>();
  private readonly records = new Map<MemoryNamespace, Map<string, MemoryRecord>>();

  constructor(
    private readonly permissions: PermissionManager,
    private readonly eventBus?: EventBus
  ) {}

  registerNamespace(namespace: MemoryNamespace): void {
    this.namespaces.add(namespace);
    if (!this.records.has(namespace)) {
      this.records.set(namespace, new Map());
    }
  }

  listNamespaces(): MemoryNamespace[] {
    return [...this.namespaces].sort();
  }

  async write<TValue = unknown>(moduleId: ModuleId, write: MemoryWrite<TValue>): Promise<MemoryRecord<TValue>> {
    this.requireNamespace(write.namespace);
    const decision = this.permissions.canAccessMemory(moduleId, write.namespace, "write");
    if (!decision.allowed) {
      throw new Error(decision.reason ?? `Module '${moduleId}' cannot write memory namespace '${write.namespace}'.`);
    }

    const now = new Date();
    const namespaceRecords = this.records.get(write.namespace)!;
    const existing = write.id ? namespaceRecords.get(write.id) : undefined;
    const record: MemoryRecord<TValue> = {
      id: write.id ?? createMemoryId(),
      namespace: write.namespace,
      value: write.value,
      createdBy: existing?.createdBy ?? moduleId,
      createdAt: existing?.createdAt ?? now,
      updatedAt: now,
      metadata: write.metadata
    };

    namespaceRecords.set(record.id, record);
    await this.eventBus?.publish("memory.written", { record }, { moduleId });
    return record;
  }

  read<TValue = unknown>(
    moduleId: ModuleId,
    namespace: MemoryNamespace,
    id: string
  ): MemoryRecord<TValue> | undefined {
    this.requireNamespace(namespace);
    const decision = this.permissions.canAccessMemory(moduleId, namespace, "read");
    if (!decision.allowed) {
      throw new Error(decision.reason ?? `Module '${moduleId}' cannot read memory namespace '${namespace}'.`);
    }

    return this.records.get(namespace)?.get(id) as MemoryRecord<TValue> | undefined;
  }

  list<TValue = unknown>(moduleId: ModuleId, namespace: MemoryNamespace): MemoryRecord<TValue>[] {
    this.requireNamespace(namespace);
    const decision = this.permissions.canAccessMemory(moduleId, namespace, "read");
    if (!decision.allowed) {
      throw new Error(decision.reason ?? `Module '${moduleId}' cannot read memory namespace '${namespace}'.`);
    }

    return [...(this.records.get(namespace)?.values() ?? [])] as MemoryRecord<TValue>[];
  }

  async delete(moduleId: ModuleId, namespace: MemoryNamespace, id: string): Promise<boolean> {
    this.requireNamespace(namespace);
    const decision = this.permissions.canAccessMemory(moduleId, namespace, "write");
    if (!decision.allowed) {
      throw new Error(decision.reason ?? `Module '${moduleId}' cannot write memory namespace '${namespace}'.`);
    }

    const deleted = this.records.get(namespace)?.delete(id) ?? false;
    if (deleted) {
      await this.eventBus?.publish("memory.deleted", { namespace, id }, { moduleId });
    }
    return deleted;
  }

  private requireNamespace(namespace: MemoryNamespace): void {
    if (!this.namespaces.has(namespace)) {
      throw new Error(`Memory namespace '${namespace}' is not registered.`);
    }
  }
}

function createMemoryId(): string {
  return `mem_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`;
}
