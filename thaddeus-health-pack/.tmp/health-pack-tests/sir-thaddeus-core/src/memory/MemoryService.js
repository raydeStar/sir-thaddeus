export class MemoryService {
    permissions;
    eventBus;
    namespaces = new Set();
    records = new Map();
    constructor(permissions, eventBus) {
        this.permissions = permissions;
        this.eventBus = eventBus;
    }
    registerNamespace(namespace) {
        this.namespaces.add(namespace);
        if (!this.records.has(namespace)) {
            this.records.set(namespace, new Map());
        }
    }
    listNamespaces() {
        return [...this.namespaces].sort();
    }
    async write(moduleId, write) {
        this.requireNamespace(write.namespace);
        const decision = this.permissions.canAccessMemory(moduleId, write.namespace, "write");
        if (!decision.allowed) {
            throw new Error(decision.reason ?? `Module '${moduleId}' cannot write memory namespace '${write.namespace}'.`);
        }
        const now = new Date();
        const namespaceRecords = this.records.get(write.namespace);
        const existing = write.id ? namespaceRecords.get(write.id) : undefined;
        const record = {
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
    read(moduleId, namespace, id) {
        this.requireNamespace(namespace);
        const decision = this.permissions.canAccessMemory(moduleId, namespace, "read");
        if (!decision.allowed) {
            throw new Error(decision.reason ?? `Module '${moduleId}' cannot read memory namespace '${namespace}'.`);
        }
        return this.records.get(namespace)?.get(id);
    }
    list(moduleId, namespace) {
        this.requireNamespace(namespace);
        const decision = this.permissions.canAccessMemory(moduleId, namespace, "read");
        if (!decision.allowed) {
            throw new Error(decision.reason ?? `Module '${moduleId}' cannot read memory namespace '${namespace}'.`);
        }
        return [...(this.records.get(namespace)?.values() ?? [])];
    }
    async delete(moduleId, namespace, id) {
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
    requireNamespace(namespace) {
        if (!this.namespaces.has(namespace)) {
            throw new Error(`Memory namespace '${namespace}' is not registered.`);
        }
    }
}
function createMemoryId() {
    return `mem_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`;
}
