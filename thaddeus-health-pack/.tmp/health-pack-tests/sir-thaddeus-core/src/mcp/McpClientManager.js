export class McpClientManager {
    eventBus;
    clients = new Map();
    toolOwners = new Map();
    constructor(eventBus) {
        this.eventBus = eventBus;
    }
    async register(client) {
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
    async unregister(clientId) {
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
    listClients() {
        return [...this.clients.keys()];
    }
    async listTools() {
        const result = [];
        for (const client of this.clients.values()) {
            const tools = await client.listTools();
            result.push(...tools.map((tool) => ({ ...tool, clientId: client.id })));
        }
        return result;
    }
    async callTool(name, args) {
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
        }
        catch (error) {
            await this.eventBus?.publish("mcp.tool_call_failed", { clientId, name, error: error instanceof Error ? error.message : String(error) });
            throw error;
        }
    }
}
