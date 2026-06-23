import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { CallToolResultSchema } from "@modelcontextprotocol/sdk/types.js";
import { McpClient, McpToolDescription } from "./McpClientManager.js";

export interface StdioMcpClientOptions {
  id: string;
  command: string;
  args?: string[];
  cwd?: string;
  env?: Record<string, string>;
}

export class StdioMcpClient implements McpClient {
  readonly id: string;
  private readonly client: Client;
  private readonly transport: StdioClientTransport;
  private connected = false;

  constructor(options: StdioMcpClientOptions) {
    this.id = options.id;
    this.client = new Client({ name: `${options.id}-client`, version: "0.1.0" });
    this.transport = new StdioClientTransport({
      command: options.command,
      args: options.args,
      cwd: options.cwd,
      env: {
        ...process.env,
        ...options.env
      } as Record<string, string>,
      stderr: "pipe"
    });
  }

  async listTools(): Promise<McpToolDescription[]> {
    await this.ensureConnected();
    const result = await this.client.listTools();
    return result.tools.map((tool) => ({
      name: tool.name,
      description: tool.description,
      inputSchema: tool.inputSchema
    }));
  }

  async callTool(name: string, args: unknown): Promise<unknown> {
    await this.ensureConnected();
    return this.client.callTool({
      name,
      arguments: isObjectRecord(args) ? args : {}
    }, CallToolResultSchema);
  }

  async close(): Promise<void> {
    if (!this.connected) {
      return;
    }

    await this.client.close();
    this.connected = false;
  }

  private async ensureConnected(): Promise<void> {
    if (this.connected) {
      return;
    }

    await this.client.connect(this.transport);
    this.connected = true;
  }
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
