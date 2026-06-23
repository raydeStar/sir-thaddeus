import { EventBus } from "../modules/EventBus.js";
import { JobHandler, JobScheduler } from "../modules/JobScheduler.js";
import { MemoryService, MemoryWrite } from "../memory/MemoryService.js";
import { McpClient, McpClientManager } from "../mcp/McpClientManager.js";
import {
  HookName,
  JobName,
  MemoryNamespace,
  ModuleId,
  ModuleManifest,
  NormalizedModuleManifest,
  ToolName
} from "../modules/ModuleManifest.js";
import { ModuleRegistry } from "../modules/ModuleRegistry.js";
import { PermissionManager } from "../modules/PermissionManager.js";
import { ToolHandler, ToolInvocation, ToolRouter } from "../modules/ToolRouter.js";

export interface ModuleImplementation {
  manifest: ModuleManifest;
  tools?: Record<ToolName, ToolHandler>;
  jobs?: Record<JobName, JobHandler>;
  hooks?: Record<HookName, HookHandler>;
}

export type HookHandler = (payload: unknown, runtime: AgentRuntime) => unknown | Promise<unknown>;

export interface AgentRuntimeSnapshot {
  modules: NormalizedModuleManifest[];
  tools: ReturnType<ToolRouter["list"]>;
  jobs: ReturnType<JobScheduler["list"]>;
  memoryNamespaces: MemoryNamespace[];
}

export class AgentRuntime {
  readonly events = new EventBus();
  readonly permissions = new PermissionManager(this.events);
  readonly modules = new ModuleRegistry(this.events);
  readonly tools = new ToolRouter(this.permissions, this.events);
  readonly jobs = new JobScheduler(this.permissions, this.events);
  readonly mcp = new McpClientManager(this.events);
  readonly memory = new MemoryService(this.permissions, this.events);

  private readonly hooks = new Map<HookName, { moduleId: ModuleId; handler: HookHandler }[]>();

  async installModule(implementation: ModuleImplementation): Promise<NormalizedModuleManifest> {
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

  async uninstallModule(moduleId: ModuleId): Promise<boolean> {
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
      } else {
        this.hooks.set(hookName, remaining);
      }
    }

    this.permissions.removeManifest(moduleId);
    return this.modules.unregister(moduleId);
  }

  approveModule(moduleId: ModuleId) {
    return this.permissions.grant(moduleId);
  }

  denyModule(moduleId: ModuleId, reason?: string) {
    return this.permissions.deny(moduleId, reason);
  }

  invokeTool<TArgs = unknown, TResult = unknown>(invocation: ToolInvocation<TArgs>): Promise<TResult> {
    return this.tools.invoke<TArgs, TResult>(invocation);
  }

  runJob<TResult = unknown>(name: JobName): Promise<TResult> {
    return this.jobs.run<TResult>(name);
  }

  registerMcpClient(client: McpClient): Promise<void> {
    return this.mcp.register(client);
  }

  callMcpTool(name: string, args: unknown): Promise<unknown> {
    return this.mcp.callTool(name, args);
  }

  writeMemory<TValue = unknown>(moduleId: ModuleId, write: MemoryWrite<TValue>) {
    return this.memory.write(moduleId, write);
  }

  readMemory<TValue = unknown>(moduleId: ModuleId, namespace: MemoryNamespace, id: string) {
    return this.memory.read<TValue>(moduleId, namespace, id);
  }

  async emitHook(
    hookName: HookName,
    payload: unknown,
    options: { requirePermission?: boolean } = {}
  ): Promise<unknown[]> {
    const handlers = this.hooks.get(hookName) ?? [];
    const results: unknown[] = [];
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

  snapshot(): AgentRuntimeSnapshot {
    return {
      modules: this.modules.list(),
      tools: this.tools.list(),
      jobs: this.jobs.list(),
      memoryNamespaces: this.memory.listNamespaces()
    };
  }
}
