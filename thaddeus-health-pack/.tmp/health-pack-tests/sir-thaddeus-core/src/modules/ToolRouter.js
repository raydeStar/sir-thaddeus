export class ToolRouter {
    permissions;
    eventBus;
    tools = new Map();
    constructor(permissions, eventBus) {
        this.permissions = permissions;
        this.eventBus = eventBus;
    }
    register(registration) {
        if (this.tools.has(registration.name)) {
            throw new Error(`Tool '${registration.name}' is already registered.`);
        }
        this.tools.set(registration.name, registration);
    }
    unregister(name) {
        return this.tools.delete(name);
    }
    list() {
        return [...this.tools.values()].map(({ moduleId, name }) => ({ moduleId, name }));
    }
    get(name) {
        const registration = this.tools.get(name);
        return registration ? { moduleId: registration.moduleId, name: registration.name } : undefined;
    }
    async invoke(invocation) {
        const registration = this.tools.get(invocation.name);
        if (!registration) {
            throw new Error(`Tool '${invocation.name}' is not registered.`);
        }
        const decision = this.permissions.canUseModule(registration.moduleId);
        if (!decision.allowed) {
            throw new Error(decision.reason ?? `Module '${registration.moduleId}' is not permitted.`);
        }
        await this.eventBus.publish("tool.invocation_started", invocation, { moduleId: registration.moduleId });
        try {
            const result = await registration.handler(invocation.args, {
                moduleId: registration.moduleId,
                requestId: invocation.requestId,
                eventBus: this.eventBus
            });
            await this.eventBus.publish("tool.invocation_completed", { name: invocation.name, requestId: invocation.requestId, result }, { moduleId: registration.moduleId });
            return result;
        }
        catch (error) {
            await this.eventBus.publish("tool.invocation_failed", {
                name: invocation.name,
                requestId: invocation.requestId,
                error: error instanceof Error ? error.message : String(error)
            }, { moduleId: registration.moduleId });
            throw error;
        }
    }
}
