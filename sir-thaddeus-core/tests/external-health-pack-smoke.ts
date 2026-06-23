import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { AgentRuntime } from "../src/assistant/AgentRuntime.js";
import { loadExternalModuleFromManifest } from "../src/modules/ExternalModuleLoader.js";

async function main(): Promise<void> {
  const repoRoot = resolve("..");
  const healthPackRoot = join(repoRoot, "thaddeus-health-pack");
  const tempDir = await mkdtemp(join(tmpdir(), "sir-thaddeus-core-health-"));

  try {
    process.env.HEALTH_STORE_PATH = join(tempDir, "health-store.json");

    const runtime = new AgentRuntime();
    const loaded = await loadExternalModuleFromManifest(
      runtime,
      join(healthPackRoot, "manifest.json")
    );

    if (loaded.manifest.id !== "com.thaddeus.health") {
      throw new Error(`Unexpected module id '${loaded.manifest.id}'.`);
    }

    await assertRejects(
      () => runtime.invokeTool({ name: "health.get_morning_strategy_brief", args: { date: "2026-06-02" } }),
      "Permissions pending"
    );

    await loaded.approve();
    const result = await runtime.invokeTool({
      name: "health.get_morning_strategy_brief",
      args: { date: "2026-06-02" }
    });

    const text = extractText(result);
    if (!text.includes("\"date\": \"2026-06-02\"")) {
      throw new Error("Expected Health Pack brief text to include requested date.");
    }

    await loaded.uninstall();
    console.log("PASS external health pack manifest loads and executes through stdio MCP");
  } finally {
    delete process.env.HEALTH_STORE_PATH;
    await rm(tempDir, { recursive: true, force: true });
  }
}

async function assertRejects(action: () => Promise<unknown>, expected: string): Promise<void> {
  try {
    await action();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (message.includes(expected)) {
      return;
    }
    throw new Error(`Expected rejection containing '${expected}', got '${message}'.`);
  }

  throw new Error(`Expected rejection containing '${expected}', but action resolved.`);
}

function extractText(result: unknown): string {
  if (
    result &&
    typeof result === "object" &&
    "content" in result &&
    Array.isArray((result as { content: unknown }).content)
  ) {
    return (result as { content: Array<{ type?: string; text?: string }> }).content
      .filter((item) => item.type === "text")
      .map((item) => item.text ?? "")
      .join("\n");
  }

  return JSON.stringify(result);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
