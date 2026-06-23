import { EventBus } from "./EventBus.js";
import { ModuleId, ToolName } from "./ModuleManifest.js";
import { PermissionManager } from "./PermissionManager.js";

export interface ToolInvocation<TArgs = unknown> {
  name: ToolName;
  args: TArgs;
  requestId?: string;
}

export interface ToolContext {
  moduleId: ModuleId;
  requestId?: string;
  eventBus: EventBus;
}

export type ToolHandler<TArgs = unknown, TResult = unknown> = (
  args: TArgs,
  context: ToolContext
) => TResult | Promise<TResult>;

export interface ToolRegistration<TArgs = unknown, TResult = unknown> {
  moduleId: ModuleId;
  name: ToolName;
  handler: ToolHandler<TArgs, TResult>;
}

export class ToolRouter {
  private readonly tools = new Map<ToolName, ToolRegistration>();

  constructor(
    private readonly permissions: PermissionManager,
    private readonly eventBus: EventBus
  ) {}

  register<TArgs = unknown, TResult = unknown>(registration: ToolRegistration<TArgs, TResult>): void {
    if (this.tools.has(registration.name)) {
      throw new Error(`Tool '${registration.name}' is already registered.`);
    }

    this.tools.set(registration.name, registration as ToolRegistration);
  }

  unregister(name: ToolName): boolean {
    return this.tools.delete(name);
  }

  list(): Omit<ToolRegistration, "handler">[] {
    return [...this.tools.values()].map(({ moduleId, name }) => ({ moduleId, name }));
  }

  get(name: ToolName): Omit<ToolRegistration, "handler"> | undefined {
    const registration = this.tools.get(name);
    return registration ? { moduleId: registration.moduleId, name: registration.name } : undefined;
  }

  async invoke<TArgs = unknown, TResult = unknown>(invocation: ToolInvocation<TArgs>): Promise<TResult> {
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
      await this.eventBus.publish(
        "tool.invocation_completed",
        { name: invocation.name, requestId: invocation.requestId, result },
        { moduleId: registration.moduleId }
      );
      return result as TResult;
    } catch (error) {
      await this.eventBus.publish(
        "tool.invocation_failed",
        {
          name: invocation.name,
          requestId: invocation.requestId,
          error: error instanceof Error ? error.message : String(error)
        },
        { moduleId: registration.moduleId }
      );
      throw error;
    }
  }
}
