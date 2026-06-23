import { resolve } from "node:path";
import { AgentRuntime } from "../assistant/AgentRuntime.js";
import { StdioMcpClient } from "../mcp/StdioMcpClient.js";
import { ModuleManifest, NormalizedModuleManifest } from "./ModuleManifest.js";
import { loadModuleManifest } from "./ModuleManifestLoader.js";
import { ToolHandler } from "./ToolRouter.js";

export interface LoadedExternalModule {
  manifest: NormalizedModuleManifest;
  approve(): Promise<unknown>;
  uninstall(): Promise<boolean>;
}

export async function loadExternalModuleFromManifest(
  runtime: AgentRuntime,
  manifestPath: string
): Promise<LoadedExternalModule> {
  const loaded = await loadModuleManifest(manifestPath);
  const execution = loaded.manifest.execution;

  if (!execution) {
    throw new Error(`Module '${loaded.manifest.id}' does not declare an execution target.`);
  }

  if (execution.type !== "stdio") {
    throw new Error(`Module '${loaded.manifest.id}' uses unsupported execution type '${execution.type}'.`);
  }

  const mcpClient = new StdioMcpClient({
    id: loaded.manifest.id,
    command: execution.command,
    args: execution.args,
    cwd: execution.cwd ? resolve(loaded.moduleRoot, execution.cwd) : loaded.moduleRoot,
    env: execution.env
  });

  const tools = Object.fromEntries(
    loaded.manifest.tools.map((toolName) => [
      toolName,
      (async (args: unknown) => runtime.callMcpTool(toolName, args)) satisfies ToolHandler
    ])
  );

  await runtime.registerMcpClient(mcpClient);
  const manifest = await runtime.installModule({
    manifest: loaded.manifest as ModuleManifest,
    tools
  });

  return {
    manifest,
    approve: () => runtime.approveModule(manifest.id),
    uninstall: async () => {
      const moduleRemoved = await runtime.uninstallModule(manifest.id);
      const clientRemoved = await runtime.mcp.unregister(manifest.id);
      return moduleRemoved || clientRemoved;
    }
  };
}
