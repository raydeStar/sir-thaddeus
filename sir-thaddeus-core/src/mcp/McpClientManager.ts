import { EventBus } from "../modules/EventBus.js";

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

export interface RegisteredMcpTool extends McpToolDescription {
  clientId: string;
}

export class McpClientManager {
  private readonly clients = new Map<string, McpClient>();
  private readonly toolOwners = new Map<string, string>();

  constructor(private readonly eventBus?: EventBus) {}

  async register(client: McpClient): Promise<void> {
    if (this.clients.has(client.id)) {
      throw new Error(`MCP client '${client.id}' is already registered.`);
    }

    const tools = await client.listTools();
    for (const tool of tools) {
      if (this.toolOwners.has(tool.name)) {
        throw new Error(`MCP tool '${tool.name}' is already provided by client '${this.toolOwners.get(tool.name)}'.`);
      }
    }

    this.clients.set(client.id, client);
    for (const tool of tools) {
      this.toolOwners.set(tool.name, client.id);
    }

    await this.eventBus?.publish("mcp.client_registered", { clientId: client.id, tools });
  }

  async unregister(clientId: string): Promise<boolean> {
    const client = this.clients.get(clientId);
    if (!client) {
      return false;
    }

    this.clients.delete(clientId);
    for (const [toolName, ownerId] of this.toolOwners) {
      if (ownerId === clientId) {
        this.toolOwners.delete(toolName);
      }
    }

    await client.close?.();
    await this.eventBus?.publish("mcp.client_unregistered", { clientId });
    return true;
  }

  listClients(): string[] {
    return [...this.clients.keys()];
  }

  async listTools(): Promise<RegisteredMcpTool[]> {
    const result: RegisteredMcpTool[] = [];
    for (const client of this.clients.values()) {
      const tools = await client.listTools();
      result.push(...tools.map((tool) => ({ ...tool, clientId: client.id })));
    }
    return result;
  }

  async callTool(name: string, args: unknown): Promise<unknown> {
    const clientId = this.toolOwners.get(name);
    if (!clientId) {
      throw new Error(`MCP tool '${name}' is not registered.`);
    }

    const client = this.clients.get(clientId);
    if (!client) {
      throw new Error(`MCP client '${clientId}' is not available.`);
    }

    await this.eventBus?.publish("mcp.tool_call_started", { clientId, name });
    try {
      const result = await client.callTool(name, args);
      await this.eventBus?.publish("mcp.tool_call_completed", { clientId, name, result });
      return result;
    } catch (error) {
      await this.eventBus?.publish(
        "mcp.tool_call_failed",
        { clientId, name, error: error instanceof Error ? error.message : String(error) }
      );
      throw error;
    }
  }
}
