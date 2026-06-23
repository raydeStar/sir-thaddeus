import { EventBus } from "../modules/EventBus.js";
import { JobScheduler } from "../modules/JobScheduler.js";
import { MemoryService } from "../memory/MemoryService.js";
import { McpClientManager } from "../mcp/McpClientManager.js";
import { ModuleRegistry } from "../modules/ModuleRegistry.js";
import { PermissionManager } from "../modules/PermissionManager.js";
import { ToolRouter } from "../modules/ToolRouter.js";
export class AgentRuntime {
    events = new EventBus();
    permissions = new PermissionManager(this.events);
    modules = new ModuleRegistry(this.events);
    tools = new ToolRouter(this.permissions, this.events);
    jobs = new JobScheduler(this.permissions, this.events);
    mcp = new McpClientManager(this.events);
    memory = new MemoryService(this.permissions, this.events);
    hooks = new Map();
    async installModule(implementation) {
        const manifest = await this.modules.register(implementation.manifest);
        this.permissions.registerManifest(manifest);
        for (const namespace of manifest.memoryNamespaces) {
            this.memory.registerNamespace(namespace);
        }
        for (const toolName of manifest.tools) {
            const handler = implementation.tools?.[toolName];
            if (handler) {
                this.tools.register({ moduleId: manifest.id, name: toolName, handler });
            }
        }
        for (const jobName of manifest.jobs) {
            const handler = implementation.jobs?.[jobName];
            if (handler) {
                this.jobs.register({ moduleId: manifest.id, name: jobName, handler });
            }
        }
        for (const hookName of manifest.hooks) {
            const handler = implementation.hooks?.[hookName];
            if (handler) {
                const handlers = this.hooks.get(hookName) ?? [];
                handlers.push({ moduleId: manifest.id, handler });
                this.hooks.set(hookName, handlers);
            }
        }
        await this.emitHook("on_module_installed", { moduleId: manifest.id }, { requirePermission: false });
        return manifest;
    }
    async uninstallModule(moduleId) {
        const manifest = this.modules.get(moduleId);
        if (!manifest) {
            return false;
        }
        for (const toolName of manifest.tools) {
            this.tools.unregister(toolName);
        }
        for (const jobName of manifest.jobs) {
            this.jobs.unregister(jobName);
        }
        for (const [hookName, handlers] of this.hooks) {
            const remaining = handlers.filter((entry) => entry.moduleId !== moduleId);
            if (remaining.length === 0) {
                this.hooks.delete(hookName);
            }
            else {
                this.hooks.set(hookName, remaining);
            }
        }
        this.permissions.removeManifest(moduleId);
        return this.modules.unregister(moduleId);
    }
    approveModule(moduleId) {
        return this.permissions.grant(moduleId);
    }
    denyModule(moduleId, reason) {
        return this.permissions.deny(moduleId, reason);
    }
    invokeTool(invocation) {
        return this.tools.invoke(invocation);
    }
    runJob(name) {
        return this.jobs.run(name);
    }
    registerMcpClient(client) {
        return this.mcp.register(client);
    }
    callMcpTool(name, args) {
        return this.mcp.callTool(name, args);
    }
    writeMemory(moduleId, write) {
        return this.memory.write(moduleId, write);
    }
    readMemory(moduleId, namespace, id) {
        return this.memory.read(moduleId, namespace, id);
    }
    async emitHook(hookName, payload, options = {}) {
        const handlers = this.hooks.get(hookName) ?? [];
        const results = [];
        const requirePermission = options.requirePermission ?? true;
        for (const entry of handlers) {
            if (requirePermission) {
                const decision = this.permissions.canUseModule(entry.moduleId);
                if (!decision.allowed) {
                    continue;
                }
            }
            await this.events.publish("hook.started", { hookName, payload }, { moduleId: entry.moduleId });
            const result = await entry.handler(payload, this);
            await this.events.publish("hook.completed", { hookName, payload, result }, { moduleId: entry.moduleId });
            results.push(result);
        }
        return results;
    }
    snapshot() {
        return {
            modules: this.modules.list(),
            tools: this.tools.list(),
            jobs: this.jobs.list(),
            memoryNamespaces: this.memory.listNamespaces()
        };
    }
}
