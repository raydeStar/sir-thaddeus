import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { fileURLToPath } from "node:url";
import { z } from "zod/v4";
import { createConfiguredHealthRuntime } from "../runtimeFactory.js";
import { healthPackManifest } from "../manifest.js";
const runtime = createConfiguredHealthRuntime();
export function createHealthPackStdioServer() {
    const server = new McpServer({
        name: "thaddeus-health-pack",
        version: healthPackManifest.version
    });
    registerJsonTool(server, "health.get_daily_snapshot", "Get the cached daily health snapshot for a date.", async ({ date }) => runtime.getDailySnapshot(date ?? todayIso()));
    registerJsonTool(server, "health.refresh_daily_snapshot", "Refresh and store a daily health snapshot from the provider.", async ({ date }) => runtime.refreshDailySnapshot(date ?? todayIso()));
    registerJsonTool(server, "health.get_baselines", "Calculate rolling personal health baselines for a date.", async ({ date }) => runtime.getBaselines(date ?? todayIso()));
    registerJsonTool(server, "health.get_morning_strategy_brief", "Generate the practical morning strategy brief for a date.", async ({ date }) => runtime.getMorningStrategyBrief(date ?? todayIso()));
    registerJsonTool(server, "health.get_similar_past_days", "Find prior days with overlapping health flags.", async ({ date }) => runtime.getSimilarPastDays(date ?? todayIso()));
    registerJsonTool(server, "health.provider_status", "Return sanitized health provider configuration/auth status.", async () => runtime.getProviderStatus());
    registerJsonTool(server, "health.provider_config_schema", "Return the sanitized provider setup schema.", async () => runtime.getProviderConfigSchema());
    registerJsonTool(server, "health.secret_store_status", "Return the local secret protection backend status.", async () => runtime.getSecretStoreStatus());
    server.registerTool("health.set_provider_config", {
        description: "Set the selected health provider and safe provider configuration. Secrets are stored in the isolated token store.",
        inputSchema: {
            providerName: z.enum(["mock", "google-health"]).optional(),
            selectedProvider: z.enum(["mock", "google-health"]).optional(),
            googleHealth: z.object({
                clientId: z.string().optional(),
                clientSecret: z.string().optional(),
                redirectUri: z.string().optional(),
                accessToken: z.string().optional(),
                refreshToken: z.string().optional(),
                apiBaseUrl: z.string().optional(),
                scopes: z.array(z.string()).optional()
            }).optional()
        }
    }, async (args) => jsonToolResult(await runtime.setProviderConfig(args)));
    registerJsonTool(server, "health.clear_provider_config", "Reset provider configuration to mock and clear stored tokens.", async () => runtime.clearProviderConfig());
    server.registerTool("health.start_provider_auth", {
        description: "Start provider OAuth and return a sanitized auth URL or missing-credentials status.",
        inputSchema: {
            state: z.string().optional()
        }
    }, async (args) => jsonToolResult(await runtime.startProviderAuth(args)));
    server.registerTool("health.complete_provider_auth", {
        description: "Complete provider OAuth with an authorization code.",
        inputSchema: {
            code: z.string(),
            state: z.string().optional()
        }
    }, async (args) => jsonToolResult(await runtime.completeProviderAuth(args)));
    registerJsonTool(server, "health.disconnect_provider", "Disconnect the configured provider and clear stored tokens.", async () => runtime.disconnectProvider());
    server.registerTool("health.provider_audit_events", {
        description: "Return recent sanitized Health Pack provider audit events.",
        inputSchema: {
            limit: z.number().int().min(1).max(100).optional()
        }
    }, async (args) => jsonToolResult(await runtime.getAuditEvents(args.limit ?? 50)));
    server.registerTool("health.sync_range", {
        description: "Sync canonical daily health snapshots for an explicit date range.",
        inputSchema: {
            startDate: z.string().describe("ISO date in YYYY-MM-DD format."),
            endDate: z.string().describe("ISO date in YYYY-MM-DD format.")
        }
    }, async (args) => jsonToolResult(await runtime.syncRange(args.startDate, args.endDate)));
    server.registerTool("health.backfill", {
        description: "Backfill canonical daily health snapshots from the configured provider.",
        inputSchema: {
            days: z.number().int().min(1).max(365).optional(),
            throughDate: z.string().optional().describe("ISO date in YYYY-MM-DD format.")
        }
    }, async (args) => jsonToolResult(await runtime.backfill(args.days ?? 30, args.throughDate ?? todayIso())));
    server.registerTool("health.log_manual_checkin", {
        description: "Log subjective, nutrition, and note fields for a daily health check-in.",
        inputSchema: {
            date: z.string().optional().describe("ISO date in YYYY-MM-DD format."),
            subjective: z.object({
                mood: z.string().optional(),
                energy: z.number().min(1).max(10).optional(),
                stress: z.number().min(1).max(10).optional(),
                soreness: z.number().min(1).max(10).optional(),
                focus: z.number().min(1).max(10).optional(),
                notes: z.string().optional()
            }).optional(),
            nutrition: z.object({
                caloriesEstimate: z.number().optional(),
                proteinEstimate: z.number().optional(),
                hydrationEstimate: z.number().optional(),
                caffeineAfterNoon: z.boolean().optional(),
                notes: z.string().optional()
            }).optional(),
            notes: z.string().optional()
        }
    }, async (args) => jsonToolResult(await runtime.logManualCheckin({
        date: args.date ?? todayIso(),
        subjective: args.subjective,
        nutrition: args.nutrition,
        notes: args.notes
    })));
    return server;
}
async function main() {
    const server = createHealthPackStdioServer();
    await server.connect(new StdioServerTransport());
    console.error("Thaddeus Health Pack MCP server running on stdio.");
}
function registerJsonTool(server, name, description, handler) {
    server.registerTool(name, {
        description,
        inputSchema: {
            date: z.string().optional().describe("ISO date in YYYY-MM-DD format.")
        }
    }, async (args) => jsonToolResult(await handler(args)));
}
function jsonToolResult(value) {
    return {
        content: [
            {
                type: "text",
                text: JSON.stringify(value, null, 2)
            }
        ],
        structuredContent: toStructuredContent(value)
    };
}
function toStructuredContent(value) {
    return value && typeof value === "object" && !Array.isArray(value)
        ? value
        : { value };
}
function todayIso() {
    return new Date().toISOString().slice(0, 10);
}
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    main().catch((error) => {
        console.error("Health Pack MCP server failed:", error instanceof Error ? error.message : String(error));
        process.exit(1);
    });
}
