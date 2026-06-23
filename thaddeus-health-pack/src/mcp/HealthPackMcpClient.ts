import type { McpClient, McpToolDescription, ToolContext } from "../coreContracts.js";
import { HealthPackRuntime, HealthPackRuntimeOptions } from "../HealthPackRuntime.js";

export class HealthPackMcpClient implements McpClient {
  readonly id = "thaddeus-health-pack";

  constructor(private readonly runtime = new HealthPackRuntime()) {}

  async listTools(): Promise<McpToolDescription[]> {
    return [
      tool("health.get_daily_snapshot", "Get the cached daily health snapshot, refreshing from the provider if needed."),
      tool("health.refresh_daily_snapshot", "Refresh and store a daily health snapshot from the configured provider."),
      tool("health.get_baselines", "Calculate rolling personal health baselines for a date."),
      tool("health.get_morning_strategy_brief", "Generate a practical morning strategy brief for a date."),
      tool("health.get_similar_past_days", "Find prior days with overlapping health flags."),
      tool("health.log_manual_checkin", "Log subjective, nutrition, or notes for a daily health check-in."),
      tool("health.provider_status", "Return sanitized health provider status."),
      tool("health.provider_config_schema", "Return the sanitized provider setup schema."),
      tool("health.secret_store_status", "Return the local secret protection backend status."),
      tool("health.set_provider_config", "Set provider selection and safe provider configuration."),
      tool("health.clear_provider_config", "Reset provider configuration and clear stored tokens."),
      tool("health.start_provider_auth", "Start provider OAuth setup."),
      tool("health.complete_provider_auth", "Complete provider OAuth setup."),
      tool("health.disconnect_provider", "Disconnect the configured provider."),
      tool("health.provider_audit_events", "Return sanitized Health Pack provider audit events."),
      tool("health.sync_range", "Sync canonical daily health snapshots for a date range."),
      tool("health.backfill", "Backfill canonical daily health snapshots from the configured provider.")
    ];
  }

  async callTool(name: string, args: unknown): Promise<unknown> {
    const handler = this.runtime.tools()[name];
    if (!handler) {
      throw new Error(`Health Pack MCP tool '${name}' is not available.`);
    }

    return handler(args, {
      moduleId: "com.thaddeus.health",
      eventBus: noopEventBus()
    });
  }
}

export function createHealthPackMcpClient(options: HealthPackRuntimeOptions = {}): HealthPackMcpClient {
  return new HealthPackMcpClient(new HealthPackRuntime(options));
}

function tool(name: string, description: string): McpToolDescription {
  return {
    name,
    description,
    inputSchema: {
      type: "object",
      additionalProperties: true,
      properties: {
        date: {
          type: "string",
          description: "ISO date in YYYY-MM-DD format."
        }
      }
    }
  };
}

function noopEventBus(): ToolContext["eventBus"] {
  return {
    subscribe: () => () => undefined,
    subscribeAll: () => () => undefined,
    publish: async (type, payload, options = {}) => ({
      type,
      payload,
      moduleId: options.moduleId,
      occurredAt: options.occurredAt ?? new Date()
    }),
    clear: () => undefined
  };
}
