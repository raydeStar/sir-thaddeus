export interface ModuleManifest {
  id: string;
  name: string;
  version: string;
  description?: string;
  permissions?: {
    externalAccounts?: Array<{ provider: string; scopes: string[] }>;
    memory?: { read?: string[]; write?: string[] };
    notifications?: string[];
  };
  tools?: string[];
  jobs?: string[];
  hooks?: string[];
  settings?: Array<{
    key: string;
    type: "string" | "number" | "boolean" | "json";
    label?: string;
    description?: string;
    defaultValue?: unknown;
    required?: boolean;
  }>;
  memoryNamespaces?: string[];
}

export type ToolHandler<TArgs = unknown, TResult = unknown> = (
  args: TArgs,
  context: ToolContext
) => TResult | Promise<TResult>;

export interface ToolContext {
  moduleId: string;
  requestId?: string;
  eventBus: {
    subscribe: (type: string, handler: (event: unknown) => void | Promise<void>) => () => void;
    subscribeAll: (handler: (event: unknown) => void | Promise<void>) => () => void;
    publish: <TPayload = unknown>(
      type: string,
      payload: TPayload,
      options?: { moduleId?: string; occurredAt?: Date }
    ) => Promise<{ type: string; payload: TPayload; moduleId?: string; occurredAt: Date }>;
    clear: () => void;
  };
}

export type JobHandler<TResult = unknown> = (context: {
  moduleId: string;
  jobName: string;
  eventBus: ToolContext["eventBus"];
}) => TResult | Promise<TResult>;

export interface ModuleImplementation {
  manifest: ModuleManifest;
  tools?: Record<string, ToolHandler>;
  jobs?: Record<string, JobHandler>;
  hooks?: Record<string, (payload: unknown, runtime: unknown) => unknown | Promise<unknown>>;
}

export interface McpToolDescription {
  name: string;
  description?: string;
  inputSchema?: unknown;
}

export interface McpClient {
  id: string;
  listTools(): Promise<McpToolDescription[]>;
  callTool(name: string, args: unknown): Promise<unknown>;
  close?(): Promise<void> | void;
}
